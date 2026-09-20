# Securite

AWS Manager est un client d'administration : ses actions ont les droits du profil
AWS selectionne et peuvent interrompre un service, supprimer des donnees ou creer
des frais. Les confirmations ne remplacent ni IAM ni les sauvegardes.

## Identifiants et contexte

- Utiliser de preference IAM Identity Center/SSO et des roles de moindre privilege.
- Les identifiants sont resolus par AWS SDK et AWS CLI, pas saisis ni enregistres
  par l'application. Leurs caches standards restent geres par ces outils.
- Les clients SDK recoivent explicitement identifiants et region du contexte verifie.
  Les commandes CLI recoivent profil et region avec des arguments separes, sans shell.
- Un ancien dialogue ne peut pas obtenir un nouveau client apres changement de contexte.
  Un appel deja parti n'est pas annule cote AWS par un changement de page/profil.
- Une expiration ne rejoue jamais une operation d'ecriture automatiquement.

## Disponibilite selon IAM

- Le grise utilise `iam:SimulatePrincipalPolicy` et `iam:GetRole` pour les roles
  assumes/SSO. Restreindre ces droits au principal concerne : une attribution trop
  large peut divulguer les droits d'autres identites. L'application ne les attribue pas.
- Refus, conditions manquantes ou evaluation indisponible laissent les fonctions
  desactivees, sans essai d'ecriture. Les decisions restent en memoire cinq minutes
  avant reevaluation a la prochaine demande et sont invalidees au changement de contexte.
- Les resultats agreges et par ressource sont lus sans transformer une absence
  de decision en autorisation. Une simulation n'est pas une garantie de succes :
  les politiques de session, de ressources, RCP, endpoints et conditions peuvent
  limiter les droits reels. IAM reste le controle de securite effectif.
- La lecture seule reste prioritaire. Les sessions SSM deja ouvertes peuvent
  toujours etre arretees localement ; elles ne sont pas coupees par cette evaluation.

## Lecture seule et historique local

- Le mode lecture seule est actif par defaut au premier lancement et son choix
  est conserve localement. Les clients SDK verifient ce mode avant chaque envoi ;
  les nouvelles sessions SSM (terminal, RDP et tunnels) sont egalement bloquees.
- Cette protection est un garde-fou de l'application, pas une restriction IAM.
  Elle ne protege pas contre un autre outil, la modification des preferences ni
  les commandes saisies dans un terminal SSM deja ouvert. Elle ne ferme aucune
  session existante et n'annule pas une operation deja envoyee a AWS.
- L'activation pendant un transfert multipart peut en bloquer les requetes
  suivantes, y compris son nettoyage. Verifier les transferts incomplets dans AWS.
- `%LOCALAPPDATA%/AwsManager/workspace.json` contient des metadonnees en clair :
  profils, comptes, regions, ressources, configurations de ports et historique.
  Les secrets AWS, valeurs de tags, corps des fichiers, liens signes et messages
  bruts d'erreur ne sont pas serialises. Ne pas saisir de secret dans les noms de
  configurations et proteger l'acces au profil Windows.
- Favoris et configurations ne sont ouverts que sous le profil, le compte et
  la region enregistres. Aucun changement AWS ni tunnel n'est lance par leur
  simple chargement.
- L'historique est local, borne, effacable et au mieux disponible. Un echec de
  journalisation ne transforme pas une operation AWS reussie en echec. Il n'est
  ni inviolable ni exhaustif et ne remplace pas CloudTrail.
- CloudWatch est consulte en lecture seule a la demande ; les donnees des courbes
  et alarmes ne sont pas stockees dans les preferences. Des couts de lecture API
  peuvent s'appliquer. Les autorisations minimales sont listees dans le README.

## Connexions et partage

- IAM est consulte sans modification des identites ou politiques et sans gestion
  de secrets. Les documents de permissions et de confiance restent en memoire ;
  ils peuvent contenir des informations internes sensibles. Leur affichage ne
  constitue pas une evaluation exhaustive des droits effectifs.
- Le changement de profil EC2 exige les droits d'association/remplacement et
  `iam:PassRole` sur le role cible. Un role excessivement privilegie peut accroitre
  les droits de tout programme ayant acces aux credentials de l'instance.
  Restreindre PassRole et verifier la confiance EC2. L'association est relue avant
  remplacement, sans detachement prealable, mais la propagation reste asynchrone
  et le changement peut faire perdre les acces applicatifs ou SSM.
- Scan et installation de correctifs sont des executions distantes soumises a
  confirmation et au mode lecture seule. Une installation requiert un scan recent
  et une baseline verifiee, sans garantie de compatibilite applicative. NoReboot
  n'empeche pas les redemarrages de services. Sauvegardes et fenetre de maintenance
  sont a verifier ; aucun rollback automatique, drainage de trafic ou orchestration
  de cluster n'est fourni. Les politiques Quick Setup ne sont pas modifiees et
  leurs remplacements de baseline ne sont pas repris dans ce parcours a la demande.
- Une commande de correctifs deja envoyee continue cote AWS apres fermeture ou
  annulation locale. Le seuil d'erreurs ne stoppe pas les machines deja en cours.
  En cas de reponse perdue, verifier Run Command avant un nouveau lancement ;
  aucun rejeu automatique n'est effectue. Les sorties sont consultables en memoire
  et peuvent contenir des informations sensibles, sans persistance dans l'historique.
- La suppression multiple S3 exige une confirmation unique et reste soumise au
  mode lecture seule et aux droits `s3:DeleteObject`. Elle ne descend pas dans
  les dossiers et ne purge pas les versions historiques. Une erreur de lot peut
  laisser un resultat partiel ; une reponse perdue exige une verification dans S3.
- Les prereglages des regles de securite ne choisissent pas de source publique.
  Toute ouverture universelle IPv4/IPv6 affiche un avertissement avant confirmation.
  Une edition conserve l'identifiant et le sens de la regle, sans revoke prealable.
- Le tunnel RDS exige un relais SSM deja autorise a atteindre la base. L'application
  ne modifie ni routes ni groupes de securite. Le port local est un point d'acces
  a proteger sur le poste ; fermer le tunnel des qu'il n'est plus necessaire.
  Les identifiants SQL ne sont pas demandes ou stockes. Configurer le chiffrement
  et la verification TLS dans le client SQL, sans desactiver la verification du
  nom du serveur pour contourner le passage par localhost.
- Les configurations RDS contiennent la reference de la base, le relais et les ports.
  L'historique d'ouverture du tunnel peut contenir son endpoint, mais aucun secret SQL.
- Les journaux CloudWatch peuvent contenir des secrets ou donnees personnelles.
  Ils restent en memoire dans l'application, sans stockage des messages ou filtres
  dans les preferences/historique, ni export automatique. La copie explicite passe
  par le presse-papiers Windows, potentiellement historise ou synchronise selon
  votre configuration. Les captures et copies doivent etre protegees.
  Le masquage AWS est conserve (`Unmask=false`), sans garantie que tous les secrets
  des messages sources aient ete masques par AWS. Utiliser IAM pour limiter la lecture.
- Seuls les processus SSM lances et suivis par cette instance de l'application
  peuvent etre arretes par la page Sessions. Le plugin doit rester a jour.
- Verifier les regles reseau avant validation, en particulier les sources universelles
  IPv4/IPv6. Tester les changements de securite sur une ressource de recette.
- Un lien S3 signe est une autorisation temporaire transmissible. Il est copie
  dans le presse-papiers ; ne pas le placer dans un ticket, un journal ou un depot public.
- Les inventaires, noms, adresses et captures peuvent etre confidentiels. Masquer
  ces donnees avant de partager un diagnostic. Ne jamais partager jeton SSO,
  secret, fichier credentials ou lien signe.

## Verification et signalement

Les tests normaux n'utilisent pas AWS. La recette distante est explicitement
activable et limitee a STS et aux lectures List/Describe ; elle ne valide pas les
mutations de production. Les limites et resultats observes figurent dans
[README.md](README.md).

Signaler un probleme au mainteneur par un canal prive disponible dans votre
organisation, ou par un avis de securite prive si le depot le propose. Fournir la
version, le parcours, les droits attendus et une reproduction anonymisee. Aucun
delai de traitement ni historique de versions supportees n'est garanti par ce document.
