using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Amazon.Runtime.CredentialManagement;

namespace AwsManager.ViewModels
{
    public class MainViewModel : ViewModelBase
    {
        private ViewModelBase? _currentViewModel;
        public ViewModelBase? CurrentViewModel
        {
            get => _currentViewModel;
            set => SetField(ref _currentViewModel, value);
        }

        public ObservableCollection<ViewModelBase> ViewModels { get; }

        public ObservableCollection<string> AwsProfiles { get; }
        private string? _selectedAwsProfile;
        public string? SelectedAwsProfile
        {
            get => _selectedAwsProfile;
            set
            {
                if (SetField(ref _selectedAwsProfile, value) && value != null)
                {
                    Environment.SetEnvironmentVariable("AWS_PROFILE", value);

                    if (CurrentViewModel is IRefreshableViewModel refreshable)
                    {
                        if (refreshable.RefreshCommand.CanExecute(null))
                        {
                            refreshable.RefreshCommand.Execute(null);
                        }
                    }
                }
            }
        }

        public MainViewModel()
        {
            AwsProfiles = new ObservableCollection<string>();
            LoadProfiles();

            ViewModels = new ObservableCollection<ViewModelBase>
            {
                new Ec2ViewModel(),
                new S3ViewModel(),
                
                new RdsViewModel(),
                new AutoScalingViewModel(),
                new SecurityGroupViewModel(),
                
                new Route53ViewModel(),
                //new LiveSessionsViewModel()
            };

            var initialVm = ViewModels.FirstOrDefault();
            if (initialVm != null)
            {
                CurrentViewModel = initialVm;
            }

            if (AwsProfiles.Any())
            {
                //SelectedAwsProfile = AwsProfiles.First();
                SelectedAwsProfile = "infra";
            }
        }

        private void LoadProfiles()
        {
            try
            {
                var sharedFile = new SharedCredentialsFile();

                var profiles = sharedFile.ListProfiles(); // retourne IEnumerable<CredentialProfile>
                foreach (var profile in profiles.OrderBy(p => p.Name))
                {
                    AwsProfiles.Add(profile.Name);
                }
            }
            catch (Exception)
            {
                // Silencieusement ignorer si le fichier est manquant ou invalide
            }
        }


    }
}
