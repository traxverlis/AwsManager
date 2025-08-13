using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Transfer;
using AwsManager.Models;

using Microsoft.WindowsAPICodePack.Dialogs;
using AwsManager.Views.Dialogs;


namespace AwsManager.ViewModels
{
    public class S3ViewModel : ViewModelBase, IRefreshableViewModel
    {
        public static string Name => "S3 Buckets";

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetField(ref _isLoading, value);
        }

        public ObservableCollection<S3ItemModel> Items { get; }
        public ICommand RefreshCommand { get; }
        public ICommand OpenItemCommand { get; }

        public ICommand DownloadFileCommand { get; }
        public ICommand DeleteFileCommand { get; }
        public ICommand UploadFileCommand { get; }

        private S3ItemModel? _selectedFile;
        public S3ItemModel? SelectedFile
        {
            get => _selectedFile;
            set => SetField(ref _selectedFile, value);
        }

        private string _currentBucket = "";
        private string _currentPrefix = "";

        private string _currentPath = "s3://";
        public string CurrentPath
        {
            get => _currentPath;
            set => SetField(ref _currentPath, value);
        }

        public S3ViewModel()
        {
            Items = [];
            RefreshCommand = new RelayCommand(async _ => await LoadBucketsAsync(), _ => !IsLoading);
            OpenItemCommand = new RelayCommand(async item => await OpenItemAsync(item), _ => !IsLoading);
            DownloadFileCommand = new RelayCommand(async _ => await DownloadFileAsync(), _ => SelectedFile != null && SelectedFile.ItemType == "File");
            DeleteFileCommand = new RelayCommand(async file => await DeleteFileAsync(), _ => SelectedFile != null && SelectedFile.ItemType == "File");
            UploadFileCommand = new RelayCommand(async _ => await UploadFileAsync(), _ => !IsLoading && !string.IsNullOrEmpty(_currentBucket));


            _ = LoadBucketsAsync();
        }

        private async Task LoadBucketsAsync()
        {
            CurrentPath = "s3://";
            _currentBucket = "";
            _currentPrefix = "";
            IsLoading = true;
            Items.Clear();
            try
            {
                using var s3Client = new AmazonS3Client();
                var response = await s3Client.ListBucketsAsync();
                foreach (var bucket in response.Buckets)
                {
                    Items.Add(new S3ItemModel { Name = bucket.BucketName, ItemType = "Bucket" });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to load S3 buckets: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsLoading = false;
            }
        }

        private async Task OpenItemAsync(object? item)
        {
            if (item is not S3ItemModel s3Item) return;

            if (s3Item.ItemType == "Bucket")
            {
                _currentBucket = s3Item.Name;
                _currentPrefix = "";
                await LoadObjectsAsync();
            }
            else if (s3Item.ItemType == "Folder")
            {
                _currentPrefix = s3Item.Key;
                await LoadObjectsAsync();
            }
            else if (s3Item.ItemType == "Navigation" && s3Item.Name == "..")
            {
                if (string.IsNullOrEmpty(_currentPrefix))
                {
                    await LoadBucketsAsync();
                }
                else
                {
                    _currentPrefix = _currentPrefix.TrimEnd('/').Substring(0, _currentPrefix.TrimEnd('/').LastIndexOf('/') + 1);
                    if (_currentPrefix.EndsWith("/") && _currentPrefix.Length == 1) _currentPrefix = "";

                    await LoadObjectsAsync();
                }
            }
            else if (s3Item.ItemType == "File")
            {
                MessageBox.Show($"This would handle file: {s3Item.Key}", "File Action", MessageBoxButton.OK, MessageBoxImage.Information);
                // Double-clicking a file can also trigger download
                SelectedFile = s3Item;
                await DownloadFileAsync();
            }
        }

        private async Task LoadObjectsAsync()
        {
            CurrentPath = $"s3://{_currentBucket}/{_currentPrefix}";
            IsLoading = true;
            Items.Clear();

            try
            {
                using var s3Client = new AmazonS3Client();
                var request = new ListObjectsV2Request
                {
                    BucketName = _currentBucket,
                    Prefix = _currentPrefix,
                    Delimiter = "/"
                };

                var response = await s3Client.ListObjectsV2Async(request);

                // Bouton ".." pour revenir en arrière
                Application.Current.Dispatcher.Invoke(() =>
                {
                    Items.Add(new S3ItemModel { Name = "..", ItemType = "Navigation" });
                });

                // Ajout des "dossiers"
                string prefix = _currentPrefix ?? "";

                foreach (var commonPrefix in response.CommonPrefixes ?? Enumerable.Empty<string>())
                {
                    var folderName = !string.IsNullOrEmpty(prefix)
                        ? commonPrefix.Replace(prefix, "").TrimEnd('/')
                        : commonPrefix.TrimEnd('/');

                    Items.Add(new S3ItemModel
                    {
                        Key = commonPrefix,
                        Name = folderName,
                        ItemType = "Folder"
                    });
                }

                foreach (var obj in (response.S3Objects ?? Enumerable.Empty<S3Object>()).Where(o => o.Key != prefix))
                {
                    var fileName = !string.IsNullOrEmpty(prefix)
                        ? obj.Key.Replace(prefix, "")
                        : obj.Key;

                    Items.Add(new S3ItemModel
                    {
                        Key = obj.Key,
                        Name = fileName,
                        ItemType = "File",
                        Size = obj.Size ?? 0,
                        LastModified = obj.LastModified
                    });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Failed to list objects in bucket '{_currentBucket}': {ex.Message}",
                    "Error",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error
                );
            }
            finally
            {
                IsLoading = false;
            }
        }


        private async Task DownloadFileAsync()
        {
            if (SelectedFile == null || SelectedFile.ItemType != "File")
            {
                System.Windows.MessageBox.Show("Please select a file to download.", "No File Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var key = SelectedFile.Key;
            var bucketName = _currentBucket;
            var fileName = Path.GetFileName(key);

            // 1️⃣ Sélecteur de dossier moderne
            string destFolder;
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = true,
                Title = "Select Destination Folder"
            };

            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                return; // L'utilisateur a annulé

            destFolder = dialog.FileName;

            // 2️⃣ Chemin complet
            var destPath = Path.Combine(destFolder, fileName);

            // 3️⃣ Confirmation si le fichier existe déjà
            if (File.Exists(destPath) &&
                MessageBox.Show($"File '{destPath}' already exists.\nOverwrite?",
                                "Confirm Overwrite",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question) == MessageBoxResult.No)
            {
                return;
            }

            // 4️⃣ Téléchargement
            try
            {
                using var transferUtility = new TransferUtility();
                await transferUtility.DownloadAsync(destPath, bucketName, key);
                MessageBox.Show($"Successfully downloaded '{key}' to '{destPath}'",
                                "Download Complete",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to download file: {ex.Message}",
                                "Download Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }
        //delete s3 file
        public async Task DeleteFileAsync()
        {
            if (SelectedFile == null || SelectedFile.ItemType != "File")
            {
                MessageBox.Show("Please select a valid file to delete.", "Invalid Selection", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var result = MessageBox.Show($"Are you sure you want to delete '{SelectedFile.Name}'?", "Confirm Deletion", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                using var s3Client = new AmazonS3Client();
                var _oldname = SelectedFile.Name;
                await s3Client.DeleteObjectAsync(new DeleteObjectRequest
                {
                    BucketName = _currentBucket,
                    Key = SelectedFile.Key
                });
                Items.Remove(SelectedFile);
                MessageBox.Show($"Successfully deleted '{_oldname}'", "Deletion Successful", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to delete file: {ex.Message}", "Deletion Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        //upload file to s3 bucket
        public async Task UploadFileAsync()
        {
            // 1️⃣ Sélecteur de fichier moderne
            var dialog = new CommonOpenFileDialog
            {
                IsFolderPicker = false,
                Title = "Select File to Upload"
            };
            if (dialog.ShowDialog() != CommonFileDialogResult.Ok)
                return; // L'utilisateur a annulé
            var filePath = dialog.FileName;
            var fileName = Path.GetFileName(filePath);
            // 2️⃣ Vérification du bucket sélectionné
            if (string.IsNullOrEmpty(_currentBucket))
            {
                MessageBox.Show("Please select a bucket first.", "No Bucket Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // 3️⃣ Téléchargement
            try
            {
                using var transferUtility = new TransferUtility();
                await transferUtility.UploadAsync(filePath, _currentBucket, _currentPrefix + fileName);
                MessageBox.Show($"Successfully uploaded '{fileName}' to bucket '{_currentBucket}'",
                                "Upload Complete",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information);
                await LoadObjectsAsync(); // Rafraîchir la liste des objets
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to upload file: {ex.Message}",
                                "Upload Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
            }
        }

    }
}
