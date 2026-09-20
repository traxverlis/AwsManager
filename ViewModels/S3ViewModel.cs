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
using System.Globalization;
using AwsManager.Services;


namespace AwsManager.ViewModels
{
    public class S3ViewModel : AwsResourceViewModel, IRefreshableViewModel
    {
        public static string Name => "S3 Buckets";

        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set { if (SetField(ref _isLoading, value)) { OnPropertyChanged(nameof(IsNotLoading)); OnPropertyChanged(nameof(IsListing)); CommandManager.InvalidateRequerySuggested(); } }
        }
        public bool IsNotLoading => !IsLoading;
        public bool IsListing => IsLoading && _transfer == null;
        private CancellationTokenSource? _transfer;
        private double _transferProgress;
        public double TransferProgress { get => _transferProgress; set => SetField(ref _transferProgress, value); }
        private int _linkMinutes = 60;
        public int LinkMinutes { get => _linkMinutes; set => SetField(ref _linkMinutes, value); }
        public ICommand RootCommand { get; }
        public ICommand CancelTransferCommand { get; }
        private string? _bucketRegion;
        private readonly Func<string, bool> _confirmDeletion;
        private S3ItemModel[] _selection = [];
        private S3ItemModel[] SelectedFiles => (_selection.Length > 0 ? _selection : SelectedFile is { } item ? [item] : [])
            .Where(item => item.ItemType == "File" && Items.Contains(item) && FilteredItems!.Contains(item)).DistinctBy(item => item.Key).ToArray();
        public int SelectedFileCount => SelectedFiles.Length;
        public string SelectionSummary => $"{SelectedFileCount} fichier(s) sélectionné(s)";
        private bool HasSingleSelection => _selection.Length <= 1 && SelectedFile?.ItemType == "File";

        public void SetSelection(IEnumerable<S3ItemModel> selection)
        {
            _selection = selection.ToArray();
            OnPropertyChanged(nameof(SelectedFileCount));
            OnPropertyChanged(nameof(SelectionSummary));
            CommandManager.InvalidateRequerySuggested();
        }

        public ObservableCollection<S3ItemModel> Items { get; }
        public ICommand RefreshCommand { get; }
        public ICommand OpenItemCommand { get; }

        public ICommand DownloadFileCommand { get; }
        public ICommand DeleteFileCommand { get; }
        public ICommand PresignFileCommand { get; }
        public ICommand UploadFileCommand { get; }

        private S3ItemModel? _selectedFile;
        public S3ItemModel? SelectedFile
        {
            get => _selectedFile;
            set { SetField(ref _selectedFile, value); OnPropertyChanged(nameof(SelectionSummary)); CommandManager.InvalidateRequerySuggested(); }
        }

        private string _currentBucket = "";
        public string CurrentBucket => _currentBucket;
        private string _currentPrefix = "";

        private string _currentPath = "s3://";
        public string CurrentPath
        {
            get => _currentPath;
            set => SetField(ref _currentPath, value);
        }

        public S3ViewModel() : this(null) { }
        public S3ViewModel(IAwsClientFactory? clientFactory, bool load = true, Func<string, bool>? confirmDeletion = null, AwsContext? context = null) : base(clientFactory, context)
        {
            _confirmDeletion = confirmDeletion ?? Confirm;
            Items = [];
            ConfigureFilter<S3ItemModel>(Items, item => $"{item.Name} {item.Key} {item.ItemType}");
            RefreshCommand = new AsyncRelayCommand(async _ => { if (string.IsNullOrEmpty(_currentBucket)) await LoadBucketsAsync(); else await LoadObjectsAsync(); }, _ => !IsLoading && Allowed(ListChecks(_currentBucket, _currentPrefix)));
            RootCommand = new AsyncRelayCommand(async _ => await LoadBucketsAsync(), _ => !IsLoading && Allowed("s3:ListAllMyBuckets"));
            CancelTransferCommand = new RelayCommand(_ => _transfer?.Cancel(), _ => _transfer != null);
            OpenItemCommand = new AsyncRelayCommand(async item => await OpenItemAsync(item), item => !IsLoading && CanOpen(item as S3ItemModel));
            DownloadFileCommand = new AsyncRelayCommand(async _ => await DownloadFileAsync(), _ => !IsLoading && HasSingleSelection && Allowed(ObjectChecks("s3:GetObject", [SelectedFile!.Key])));
            DeleteFileCommand = new AsyncRelayCommand(async _ => await DeleteFileAsync(), _ => !IsLoading && SelectedFileCount > 0 && !string.IsNullOrEmpty(_currentBucket) && Allowed(ObjectChecks("s3:DeleteObject", SelectedFiles.Select(item => item.Key)), true));
            PresignFileCommand = new AsyncRelayCommand(async _ => await PresignFileAsync(), _ => !IsLoading && HasSingleSelection && Allowed(ObjectChecks("s3:GetObject", [SelectedFile!.Key])));
            UploadFileCommand = new AsyncRelayCommand(async _ => await UploadFileAsync(), _ => !IsLoading && !string.IsNullOrEmpty(_currentBucket) && Allowed(ObjectChecks("s3:PutObject", [_currentPrefix + "*"]), true));


            if (load) _ = LoadBucketsAsync();
        }

        private PermissionCheck[] ListChecks(string bucket, string prefix) => bucket.Length == 0 ? [new("s3:ListAllMyBuckets")] :
            [new("s3:GetBucketLocation", $"arn:{Partition}:s3:::{bucket}"), new("s3:ListBucket", $"arn:{Partition}:s3:::{bucket}", "s3:prefix", prefix)];
        private IEnumerable<PermissionCheck> ObjectChecks(string action, IEnumerable<string> keys) =>
            keys.Select(key => new PermissionCheck(action, $"arn:{Partition}:s3:::{_currentBucket}/{key}", Region: _bucketRegion))
                .Prepend(new("s3:GetBucketLocation", $"arn:{Partition}:s3:::{_currentBucket}"));
        private bool CanOpen(S3ItemModel? item)
        {
            if (item == null) return false;
            if (item.ItemType == "File") return Allowed(ObjectChecks("s3:GetObject", [item.Key]));
            if (item.ItemType == "Navigation")
            {
                if (_currentPrefix.Length == 0) return Allowed("s3:ListAllMyBuckets");
                var prefix = _currentPrefix.TrimEnd('/');
                return Allowed(ListChecks(_currentBucket, prefix[..(prefix.LastIndexOf('/') + 1)]));
            }
            return Allowed(ListChecks(item.ItemType == "Bucket" ? item.Name : _currentBucket, item.ItemType == "Folder" ? item.Key : ""));
        }

        private async Task LoadBucketsAsync()
        {
            Status = "";
            CurrentPath = "s3://";
            _currentBucket = "";
            _currentPrefix = "";
            _bucketRegion = null;
            SelectedFile = null;
            IsLoading = true;
            SetSelection([]);
            Items.Clear();
            try
            {
                using var s3Client = ClientFactory.CreateS3Client();
                await foreach (var bucket in s3Client.Paginators.ListBuckets(new ListBucketsRequest()).Buckets)
                {
                    Items.Add(new S3ItemModel { Name = bucket.BucketName, ItemType = "Bucket" });
                }
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task<bool> SelectReferenceAsync(ResourceReference resource)
        {
            if (IsLoading) throw new InvalidOperationException("Attendez la fin du chargement S3.");
            if (string.IsNullOrEmpty(resource.ParentId))
            {
                await LoadBucketsAsync();
                SelectedFile = Items.FirstOrDefault(item => item.Name == resource.Id);
            }
            else
            {
                _currentBucket = resource.ParentId;
                _bucketRegion = null;
                var key = resource.Id.EndsWith('/') ? resource.Id[..^1] : resource.Id;
                _currentPrefix = key[..(key.LastIndexOf('/') + 1)];
                await LoadObjectsAsync();
                SelectedFile = Items.FirstOrDefault(item => item.Key == resource.Id);
            }
            return SelectedFile != null;
        }

        private async Task OpenItemAsync(object? item)
        {
            if (item is not S3ItemModel s3Item) return;

            if (s3Item.ItemType == "Bucket")
            {
                _currentBucket = s3Item.Name;
                _currentPrefix = "";
                _bucketRegion = null;
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
                    _currentPrefix = _currentPrefix.TrimEnd('/')[..(_currentPrefix.TrimEnd('/').LastIndexOf('/') + 1)];
                    if (_currentPrefix.EndsWith('/') && _currentPrefix.Length == 1) _currentPrefix = "";

                    await LoadObjectsAsync();
                }
            }
            else if (s3Item.ItemType == "File")
            {
                // Double-clicking a file can also trigger download
                SelectedFile = s3Item;
                await DownloadFileAsync();
            }
        }

        private async Task LoadObjectsAsync()
        {
            Status = "";
            CurrentPath = $"s3://{_currentBucket}/{_currentPrefix}";
            IsLoading = true;
            SetSelection([]);
            Items.Clear();
            SelectedFile = null;

            try
            {
                using var s3Client = await CreateBucketClientAsync();
                var request = new ListObjectsV2Request
                {
                    BucketName = _currentBucket,
                    Prefix = _currentPrefix,
                    Delimiter = "/"
                };

                Items.Add(new S3ItemModel { Name = "..", ItemType = "Navigation" });
                do
                {
                    var response = await s3Client.ListObjectsV2Async(request);

                    // Ajout des "dossiers"
                    string prefix = _currentPrefix ?? "";

                    foreach (var commonPrefix in response.CommonPrefixes ?? Enumerable.Empty<string>())
                    {
                        var folderName = !string.IsNullOrEmpty(prefix)
                            ? ResourceValidation.RelativeKey(commonPrefix, prefix).TrimEnd('/')
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
                            ? ResourceValidation.RelativeKey(obj.Key, prefix)
                            : obj.Key;

                        Items.Add(new S3ItemModel
                        {
                            Key = obj.Key,
                            Name = fileName,
                            ItemType = "File",
                            Size = ResourceValidation.FileSize(obj.Size ?? 0),

                            LastModified = obj.LastModified
                        });
                    }
                    request.ContinuationToken = response.NextContinuationToken;
                } while (!string.IsNullOrEmpty(request.ContinuationToken));
            }
            catch (Exception ex)
            {
                ReportError(ex);
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
                IsLoading = true;
                TransferProgress = 0;
                using var cancellation = new CancellationTokenSource();
                _transfer = cancellation;
                OnPropertyChanged(nameof(IsListing));
                var progress = new Progress<double>(value => TransferProgress = value);
                using var client = await CreateBucketClientAsync();
                using var transferUtility = new TransferUtility(client);
                var request = new TransferUtilityDownloadRequest { FilePath = destPath, BucketName = bucketName, Key = key };
                request.WriteObjectProgressEvent += (_, args) => ((IProgress<double>)progress).Report(args.PercentDone);
                await transferUtility.DownloadAsync(request, cancellation.Token);
                if (Context != null) OperationSafety.Record(Context, "S3", "DownloadObject", $"{bucketName}/{key}", "Telechargement termine");
                AwsManager.Services.NotificationService.Publish($"Successfully downloaded '{key}' to '{destPath}'");
            }
            catch (OperationCanceledException) { NotificationService.Publish("Telechargement annule. Un fichier partiel peut subsister dans le dossier de destination."); }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally { _transfer = null; IsLoading = false; }
        }
        public async Task DeleteFileAsync()
        {
            if (!Allowed(ObjectChecks("s3:DeleteObject", SelectedFiles.Select(item => item.Key)), true)) return;
            if (IsLoading || string.IsNullOrEmpty(_currentBucket)) return;
            var selected = SelectedFiles;
            if (selected.Length == 0) return;
            var bucket = _currentBucket;
            var preview = string.Join("\n", selected.Take(12).Select(item => item.Key));
            var remaining = selected.Length > 12 ? $"\n... et {selected.Length - 12} autre(s)." : "";
            if (!_confirmDeletion($"Supprimer {selected.Length} fichier(s) dans s3://{bucket} ?\n\n{preview}{remaining}\n\nLes dossiers et buckets sont exclus. Aucune suppression recursive.\nSans versioning, la suppression est definitive ; les anciennes versions ne sont pas purgees.")) return;
            var deletedCount = 0;
            var errorCodes = new HashSet<string>();
            try
            {
                IsLoading = true;
                using var s3Client = await CreateBucketClientAsync();
                foreach (var batch in selected.Chunk(1000))
                {
                    DeleteObjectsResponse response;
                    try
                    {
                        response = await s3Client.DeleteObjectsAsync(new DeleteObjectsRequest
                        {
                            BucketName = bucket, Quiet = false,
                            Objects = batch.Select(item => new KeyVersion { Key = item.Key }).ToList()
                        });
                    }
                    catch (DeleteObjectsException exception) { response = exception.Response; }
                    var errors = (response.DeleteErrors ?? []).Select(error => error.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (var error in response.DeleteErrors ?? []) errorCodes.Add(error.Code ?? "Erreur AWS");
                    var deleted = (response.DeletedObjects ?? []).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
                    foreach (var item in batch.Where(item => deleted.Contains(item.Key) && !errors.Contains(item.Key)))
                    {
                        Items.Remove(item);
                        deletedCount++;
                    }
                }
                Status = $"{deletedCount}/{selected.Length} fichier(s) supprimé(s).";
                if (deletedCount != selected.Length) Status += " Les fichiers non confirmés restent dans la liste. " + string.Join(", ", errorCodes);
                NotificationService.Publish(Status);
            }
            catch (Exception ex)
            {
                ReportError(ex);
                Status = $"{deletedCount}/{selected.Length} suppression(s) confirmée(s). {Status} Actualisez avant de réessayer : le dernier lot peut avoir été traité.";
            }
            finally
            {
                if (SelectedFile != null && !Items.Contains(SelectedFile)) SelectedFile = null;
                SetSelection(selected.Where(Items.Contains));
                IsLoading = false;
            }
        }

        //upload file to s3 bucket
        public async Task UploadFileAsync()
        {
            if (!Allowed(ObjectChecks("s3:PutObject", [_currentPrefix + "*"]), true)) return;
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
            if (!Confirm($"Envoyer {fileName} vers s3://{_currentBucket}/{_currentPrefix} ?\nUn objet portant la meme cle sera remplace.")) return;
            try
            {
                IsLoading = true;
                TransferProgress = 0;
                using var cancellation = new CancellationTokenSource();
                _transfer = cancellation;
                OnPropertyChanged(nameof(IsListing));
                var progress = new Progress<double>(value => TransferProgress = value);
                using var client = await CreateBucketClientAsync();
                using var transferUtility = new TransferUtility(client);
                var request = new TransferUtilityUploadRequest { FilePath = filePath, BucketName = _currentBucket, Key = _currentPrefix + fileName };
                request.UploadProgressEvent += (_, args) => ((IProgress<double>)progress).Report(args.PercentDone);
                await transferUtility.UploadAsync(request, cancellation.Token);
                AwsManager.Services.NotificationService.Publish($"Successfully uploaded '{fileName}' to bucket '{_currentBucket}'");
                await LoadObjectsAsync(); // Rafraîchir la liste des objets
            }
            catch (OperationCanceledException) { NotificationService.Publish("Envoi annule. Verifiez l'objet et les parties multipart eventuellement restantes dans S3."); }
            catch (Exception ex)
            {
                ReportError(ex);
            }
            finally { _transfer = null; IsLoading = false; }
        }

        private async Task PresignFileAsync()
        {
            if (SelectedFile == null || SelectedFile.ItemType != "File")
            {
                System.Windows.MessageBox.Show("Please select a file to download.", "No File Selected", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var key = SelectedFile.Key;
            var bucketName = _currentBucket;
            var fileName = Path.GetFileName(key);

            try
            {
                if (LinkMinutes < 1 || LinkMinutes > 1440) throw new ArgumentException("Duree du lien : de 1 a 1440 minutes.");
                using var s3Client = await CreateBucketClientAsync();
                var request = new GetPreSignedUrlRequest
                {
                    BucketName = bucketName,
                    Key = key,
                    Expires = DateTime.UtcNow.AddMinutes(LinkMinutes)
                };
                var url = s3Client.GetPreSignedURL(request);

                //add url to clipboard
                Clipboard.SetText(url);
                if (Context != null) OperationSafety.Record(Context, "S3", "PresignGetObject", $"{bucketName}/{key}", "Lien copie (URL non conservee)");


                // Afficher l'URL dans une boîte de dialogue

                NotificationService.Publish($"Lien copie. Duree maximale : {LinkMinutes} minutes, limitee par l'expiration des identifiants AWS.");
                /*var presignDialog = new PresignUrlDialog(url);
                presignDialog.Title = "Presigned URL";
                presignDialog.ShowDialog();*/
            }
            catch (Exception ex)
            {
                ReportError(ex);
            }

        }

        private async Task<IAmazonS3> CreateBucketClientAsync()
        {
            if (_bucketRegion == null)
            {
                using var client = ClientFactory.CreateS3Client();
                var location = await client.GetBucketLocationAsync(new GetBucketLocationRequest { BucketName = _currentBucket });
                _bucketRegion = location.Location?.Value;
                if (string.IsNullOrEmpty(_bucketRegion)) _bucketRegion = "us-east-1";
                if (_bucketRegion == "EU") _bucketRegion = "eu-west-1";
            }
            return ClientFactory.CreateS3Client(_bucketRegion);
        }

    }
}
