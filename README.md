# ErrorChecker – assistance à distance sans serveur

Un utilisateur bloqué demande de l'aide en un clic ; le dépanneur reçoit un mail, ouvre la session
avec le code reçu, l'utilisateur accepte, et le dépanneur voit l'écran et prend la main.
Tout passe par un **dossier partagé** (fichiers) et **Outlook** (notification) : aucun serveur à installer.

## Installation

1. Publier l'application et la déposer avec sa configuration dans un dossier accessible à tous, par exemple
   `\\serveur\outils\ErrorChecker\` :
   ```
   dotnet publish ErrorChecker.csproj -c Release -r win-x64 --self-contained false
   ```
   ≈ 2 Mo, mais il faut le runtime « .NET 6 Desktop » sur chaque poste. Sinon, un seul exécutable autonome
   (≈ 70 Mo, plus lent à lancer depuis le réseau) :
   ```
   dotnet publish ErrorChecker.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
   ```
2. Adapter `ErrorChecker.json` à côté de l'exécutable :
   ```json
   {
     "SharedFolder": "\\\\serveur\\partage\\ErrorChecker",
     "Helpers": [
       { "Name": "Gianni", "Email": "gianni@societe.fr" },
       { "Name": "Support N2", "Email": "support@societe.fr" }
     ]
   }
   ```
   `SharedFolder` : lecture/écriture pour les utilisateurs et le support (chemin UNC).
   Le premier dépanneur est proposé par défaut ; avec un seul, l'utilisateur n'a rien à choisir.

## Utilisation

**Utilisateur** : lancer `ErrorChecker.exe`, cliquer sur « Demander de l'aide », accepter quand le
dépanneur se connecte. Le bouton « Terminer l'assistance » reste visible pendant toute la session.

Depuis une application VBA, la demande peut partir **sans aucun clic**, avec le contexte de l'erreur :

```vba
Shell """\\serveur\outils\ErrorChecker\ErrorChecker.exe"" /aide ""Erreur " & Err.Number & " : " & Err.Description & """", vbNormalFocus
```

**Dépanneur** : cliquer sur le code dans le mail (ouvre directement la session), ou lancer
`ErrorChecker.exe /depanneur` et taper le code (`K7QM-2XPA-9TRD`, sans se soucier des majuscules ni des tirets).
Cliquer dans l'image pour prendre la main : le cadre rouge indique que le clavier est transmis
(raccourcis compris : Maj+F8, Alt+F11, Ctrl+Pause…). Boutons de raccourcis et envoi de texte dans la barre d'outils.

Réglages côté dépanneur : écran, cadence maximale (1 à 15 images/s), qualité. La cadence **baisse toute
seule** si le réseau ou le poste ne suit pas (au plus 2 images en attente d'affichage) : pas de retard qui
s'accumule. La barre d'état affiche images/s, débit, retard image et aller-retour.

## Comment ça marche (et pourquoi c'est léger)

- **Transport** : un fichier journal par sens, lu au fil de l'eau via un handle ouvert (pas de liste de dossier,
  sujette aux caches SMB de 5 à 10 s), chiffré AES-GCM. La clé et le nom du dossier sont dérivés du code (PBKDF2) :
  rien de secret n'est écrit sur le partage.
- **Image** : seuls les pixels modifiés partent (rectangle serré dans les tuiles 64×64 touchées) ; un défilement est
  envoyé comme « recopier ces lignes » ; le texte et l'interface (peu de couleurs) passent par une palette **sans
  perte** compressée en Brotli, seules les colonnes riches (photo, icônes) en JPEG ; une zone envoyée en JPEG puis
  immobile est renvoyée nette après 0,7 s ; encodage et décodage en parallèle.

  Mesuré sur 16 captures réelles (Excel, éditeurs VBA/SQL, dialogues, Outlook, web, fonds photo ; JPEG d'ImageSharp
  en remplacement de GDI+) : image complète ≈ 63 Ko en moyenne, saisie dans une cellule ≈ 330 octets,
  défilement de 20 px ≈ 4 Ko.
- **Réactivité** : capture rapprochée juste après un clic ou une touche du dépanneur ; envoi immédiat des actions.

## Limites

- Ctrl+Alt+Suppr, l'écran UAC et les applications lancées en administrateur ne peuvent pas être pilotés.
- Le lien `errorchecker:` doit être cliquable dans Outlook (classique) ; sinon, taper le code.
- Une session = un dépanneur ; pour recommencer, l'utilisateur refait une demande.

## Tests

```
dotnet test tests/ErrorChecker.Tests
```
(protocole, canal fichier, diff, défilement, codec palette, et simulation complète vérifiant que l'écran du
dépanneur reste identique à celui de l'utilisateur ; s'exécute aussi sous Linux.)
