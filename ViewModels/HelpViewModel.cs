using System.Globalization;
using System.Windows.Input;
using MaterialDesignThemes.Wpf;

namespace AwsManager.ViewModels;

public sealed record HelpSection(string Title, string Text);
public sealed record HelpTopic(string Title, string Category, PackIconKind Icon, string Summary, HelpSection[] Sections, string Note);

public sealed class HelpViewModel : ViewModelBase
{
    private readonly HelpTopic[] _allTopics;
    private IReadOnlyList<HelpTopic> _topics;
    private HelpTopic? _selectedTopic;
    private string _searchText = "";

    public HelpViewModel(string? message = null)
    {
        _allTopics = [
            new("Connexion AWS", "Compte et r\u00e9gion", PackIconKind.CloudOutline,
                "Une identit\u00e9 v\u00e9rifi\u00e9e, un compte et une r\u00e9gion pour chaque op\u00e9ration.", [
                new("Profils locaux", "Les profils proviennent de la configuration AWS du poste. Les profils SSO reposent sur AWS CLI v2 ; les identifiants ne sont pas saisis dans AWS Manager."),
                new("Identit\u00e9 et r\u00e9gion", "La v\u00e9rification STS confirme le compte et le r\u00f4le actifs. La r\u00e9gion d\u00e9termine les inventaires r\u00e9gionaux ; IAM et Route 53 sont des services globaux."),
                new("Changement de contexte", "Un changement de profil ou de r\u00e9gion invalide les inventaires et les droits pr\u00e9c\u00e9dents. Les sessions SSM d\u00e9j\u00e0 ouvertes restent rattach\u00e9es \u00e0 leur contexte d'origine.")
            ], "Le compte affich\u00e9 fait foi, pas le nom donn\u00e9 au profil."),
            new("Droits et s\u00e9curit\u00e9", "Permissions IAM", PackIconKind.ShieldCheck,
                "Trois \u00e9tats de permission, distincts du mode lecture seule.", [
                new("Autoris\u00e9, refus\u00e9, non v\u00e9rifi\u00e9", "Autoris\u00e9 : la simulation IAM accepte l'action. Refus\u00e9 : elle la refuse. Non v\u00e9rifi\u00e9 : une condition, une r\u00e9ponse ou l'acc\u00e8s \u00e0 la simulation manque. Seul l'\u00e9tat autoris\u00e9 active une fonction AWS."),
                new("Pr\u00e9requis de la simulation", "iam:SimulatePrincipalPolicy est requis sur le principal concern\u00e9, ainsi que iam:GetRole pour un r\u00f4le assum\u00e9 ou SSO. Les r\u00e9sultats sont conserv\u00e9s cinq minutes en m\u00e9moire ; le bouton bouclier permet une r\u00e9\u00e9valuation explicite."),
                new("Lecture seule", "Ce r\u00e9glage bloque les modifications et les nouvelles connexions SSM, m\u00eame avec un r\u00f4le administrateur. L'arr\u00eat local d'une session existante reste disponible.")
            ], "La simulation n'est pas une garantie d'acc\u00e8s : AWS applique les politiques effectives lors de chaque requ\u00eate."),
            new("Ressources", "Inventaires et modifications", PackIconKind.Database,
                "Des op\u00e9rations li\u00e9es \u00e0 une cible, \u00e0 son \u00e9tat et \u00e0 ses permissions.", [
                new("EC2, RDS et Auto Scaling", "Les actions d\u00e9pendent de la ressource s\u00e9lectionn\u00e9e et de son \u00e9tat. Un profil IAM d'instance EC2 n'est pas le profil de connexion de l'utilisateur ; son remplacement peut interrompre les acc\u00e8s de l'instance."),
                new("Objets S3", "La suppression multiple concerne les objets s\u00e9lectionn\u00e9s, pas les dossiers ni les buckets. Elle ne purge pas les anciennes versions. Une URL pr\u00e9sign\u00e9e donne temporairement acc\u00e8s \u00e0 un objet."),
                new("R\u00e9seau et DNS", "Les r\u00e8gles de s\u00e9curit\u00e9 distinguent entr\u00e9es, sorties, protocoles et sources. Certaines configurations DNS avanc\u00e9es restent en consultation pour pr\u00e9server leurs attributs.")
            ], "Une demande accept\u00e9e par AWS ne signifie pas que la modification est d\u00e9j\u00e0 propag\u00e9e."),
            new("Sessions SSM", "Terminaux et tunnels", PackIconKind.Console,
                "Des acc\u00e8s temporaires via Systems Manager, sans ouverture automatique de ports entrants.", [
                new("Pr\u00e9requis", "AWS CLI v2 et le plugin Session Manager doivent \u00eatre disponibles sur le poste. La cible doit \u00eatre g\u00e9r\u00e9e par SSM, en ligne, avec un agent compatible et les permissions requises."),
                new("Tunnel RDS", "Le relais est une instance EC2 joignable par SSM et capable d'atteindre la base. Le port local donne acc\u00e8s au tunnel ; l'authentification de la base et ses exigences TLS restent inchang\u00e9es."),
                new("Dur\u00e9e de vie", "Les sessions suivies sont celles lanc\u00e9es par AWS Manager. Un changement de profil ne les ferme pas. Une configuration enregistr\u00e9e ne contient pas de mot de passe.")
            ], "La fermeture d'un tunnel coupe les connexions qui l'utilisent."),
            new("Correctifs SSM", "Maintenance", PackIconKind.Wrench,
                "Une pr\u00e9paration, un scan et une installation sont des \u00e9tapes distinctes.", [
                new("Pr\u00e9paration", "La baseline, l'\u00e9tat des cibles et les pr\u00e9requis sont relus avant envoi. La pr\u00e9paration n'installe rien et ne reste valable que cinq minutes."),
                new("Scan et installation", "L'installation exige un scan r\u00e9cent, de moins de 24 heures, avec la m\u00eame baseline, ainsi que la validation des sauvegardes et de la maintenance. Le document utilis\u00e9 est AWS-RunPatchBaseline."),
                new("Ex\u00e9cution distante", "NoReboot est le choix initial, mais des services peuvent red\u00e9marrer. Fermer une fen\u00eatre ou annuler une attente locale n'annule pas une commande d\u00e9j\u00e0 envoy\u00e9e \u00e0 AWS.")
            ], "Un scan est lui aussi une commande distante. Aucun retour arri\u00e8re automatique n'est garanti."),
            new("D\u00e9pannage", "Diagnostic", PackIconKind.HelpCircleOutline,
                "Distinguer authentification, autorisation et disponibilit\u00e9 de la cible.", [
                new("Session expir\u00e9e", "Une session SSO expir\u00e9e n\u00e9cessite une nouvelle authentification. Un refus AccessDenied rel\u00e8ve des permissions ; une reconnexion seule ne le corrige pas."),
                new("Bouton gris\u00e9", "Les causes possibles sont la lecture seule, un droit refus\u00e9 ou non v\u00e9rifi\u00e9, l'absence de s\u00e9lection, une cible incompatible ou une op\u00e9ration en cours. Le statut IAM et les infobulles de navigation pr\u00e9cisent les droits."),
                new("Inventaire ou connexion indisponible", "Un mauvais compte, une autre r\u00e9gion ou un filtre peuvent expliquer une liste vide. Pour SSM, la disponibilit\u00e9 du plugin local et l'\u00e9tat en ligne de l'agent sont des pr\u00e9requis distincts.")
            ], "Les cl\u00e9s d'acc\u00e8s, jetons, URL sign\u00e9es et mots de passe ne doivent pas figurer dans un rapport de diagnostic."),
            new("\u00c0 propos", "AWS Manager", PackIconKind.InformationOutline,
                "Application de bureau Windows con\u00e7ue par Julien Cosso.", [
                new("Environnement", "Interface WPF sur .NET 10, avec les SDK AWS et Material Design. L'aide est embarqu\u00e9e dans l'application et reste disponible hors connexion."),
                new("Donn\u00e9es locales", "Les favoris, configurations d'acc\u00e8s et l'historique local sont enregistr\u00e9s dans %LOCALAPPDATA%\\AwsManager. Les identifiants AWS restent g\u00e9r\u00e9s par les m\u00e9canismes AWS du poste."),
                new("Confidentialit\u00e9", "Les documents de politiques IAM et le contenu des journaux CloudWatch consult\u00e9s ne sont pas persist\u00e9s dans les pr\u00e9f\u00e9rences. Les m\u00e9tadonn\u00e9es de ressources peuvent n\u00e9anmoins \u00eatre sensibles.")
            ], "Les autorisations du compte AWS et les r\u00e8gles de votre organisation restent applicables.")
        ];
        if (!string.IsNullOrWhiteSpace(message))
            _allTopics = [new("Note", "Contexte", PackIconKind.InformationOutline, "", [new("Information", message)], ""), .. _allTopics];
        _topics = _allTopics;
        _selectedTopic = _topics[0];
        ClearSearchCommand = new RelayCommand(_ => SearchText = "");
    }

    public IReadOnlyList<HelpTopic> Topics => _topics;
    public HelpTopic? SelectedTopic
    {
        get => _selectedTopic;
        set { if (SetField(ref _selectedTopic, value)) OnPropertyChanged(nameof(HasSelection)); }
    }
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (!SetField(ref _searchText, value)) return;
            var query = value.Trim();
            bool Matches(string text) => CultureInfo.GetCultureInfo("fr-FR").CompareInfo.IndexOf(text, query, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
            _topics = _allTopics.Where(topic => Matches(topic.Title) || Matches(topic.Category) || Matches(topic.Summary) ||
                Matches(topic.Note) || topic.Sections.Any(section => Matches(section.Title) || Matches(section.Text))).ToArray();
            var selection = _topics.Contains(SelectedTopic) ? SelectedTopic : _topics.FirstOrDefault();
            OnPropertyChanged(nameof(Topics));
            SelectedTopic = selection;
            OnPropertyChanged(nameof(HasResults));
            OnPropertyChanged(nameof(HasSearch));
            OnPropertyChanged(nameof(ResultLabel));
        }
    }
    public bool HasSelection => SelectedTopic != null;
    public bool HasResults => Topics.Count != 0;
    public bool HasSearch => SearchText.Length != 0;
    public string ResultLabel => $"{Topics.Count} rubrique{(Topics.Count == 1 ? "" : "s")}";
    public string VersionLabel => $"Version {typeof(HelpViewModel).Assembly.GetName().Version?.ToString(3)}";
    public ICommand ClearSearchCommand { get; }
}