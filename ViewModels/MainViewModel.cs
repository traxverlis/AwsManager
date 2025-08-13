using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using System.Windows;
using Amazon.Runtime.CredentialManagement;
using AwsManager.Views.Dialogs;

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
        public ICommand QuitCommand { get; }
        public ICommand HelpCommand { get; }

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

            QuitCommand = new RelayCommand(CloseApp, _ => true);
            HelpCommand = new RelayCommand(Help, _ => true);


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

        // add QuitCommand


        // add CloseApp
        private void CloseApp(object? parameter)
        {
            // Ferme l'application comme si appui sur la croix de la fenêtre
            if (App.Current.MainWindow != null)
            {
                App.Current.MainWindow.Close();



            }
        }

        private void Help(object? parameter)
        {
            // Ouvre la page d'aide avec un texte personnalisé
            var helpText = parameter as string ?? @"*******************************
LICENCE LOGICIELLE : ""AWS Manager""
*******************************

**Préambule**  
Le présent document régit l’utilisation du logiciel ""AWS Manager"".  
En accédant ou en utilisant ce programme, vous acceptez de respecter les dispositions suivantes,  
ainsi que toutes les lois applicables, y compris celles que nous inventerons éventuellement plus tard.

---

### Article 1 – Objet
Ce logiciel est conçu dans le but précis de simplifier les opérations AWS.  
Toute utilisation sortant de ce cadre devra être validée par écrit… ou par télépathie,  
selon la disponibilité de notre service paranormal.

---

### Article 2 – Conditions d’utilisation
- L’utilisateur s’engage à utiliser le logiciel de manière responsable, éthique, et en évitant  
  toute interaction hostile avec des entités extraterrestres non enregistrées.  
- Tout détournement à des fins de transformation de votre PC en grille-pain sera considéré  
  comme une violation grave (et légèrement croustillante) de cette licence.

---

### Article 3 – Obligations de l’utilisateur
- Lire cette licence en entier (oui, même les petites lignes que vous pensiez ignorer).  
- Afficher un sourire sincère ou, à défaut, un rictus poli.  
- Ne pas utiliser le logiciel pour prédire l’avenir, sauf si le résultat est favorable à notre équipe.

---

### Article 4 – Interdictions spécifiques
- Utiliser le logiciel comme arme dans un tournoi clandestin de Mario Kart.  
- Organiser un karaoké d’entreprise exclusivement composé de musiques des années 80,  
  sauf si vous nous invitez.  
- Créer un serveur de jeu rétro dans le but de ""tester la performance réseau"" (on vous voit venir).

---

### Article 5 – Clause sérieuse
La responsabilité des auteurs ne saurait être engagée en cas de :  
- Fous rires incontrôlés.  
- Danses de la victoire exécutées sur un bureau instable.  
- Éloges exagérés envers nos compétences de développeurs.

---

### Article 6 – Acceptation
En cliquant sur ""Accepter"", vous reconnaissez avoir lu et compris cette licence,  
et acceptez de respecter l’ensemble de ses termes, même les plus farfelus.  
Vous reconnaissez également que ce texte est juridiquement contraignant…  
dans un univers parallèle où les licornes exercent comme avocats.

*******************************";
            var helpWindow = new HelpWindow
            {
                DataContext = helpText,
                Owner = Application.Current.MainWindow
            };

            helpWindow.Show();
        }
    }
}
