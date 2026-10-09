# ErrorChecker – assistance à distance sans serveur

Un utilisateur bloqué demande de l'aide en un clic ; le dépanneur reçoit un mail, ouvre la session
avec le code reçu, l'utilisateur accepte, et le dépanneur voit l'écran et prend la main.
Tout passe par un **dossier partagé** (fichiers) et **Outlook** (notification) : aucun serveur à installer.

## Installation

1. Compiler et déposer le dossier de sortie dans un dossier accessible à tous, par exemple
   `\\serveur\outils\ErrorChecker\` :
   ```
   dotnet build ErrorChecker.csproj -c Release
   ```
   (dossier `bin/Release/net48/`). L'application cible **.NET Framework 4.8**, intégré à Windows 11 et à
   Windows 10 depuis la version 1903 (mai 2019) : rien à installer sur ces postes. Windows 10 LTSC 2019 et
   plus anciens sont livrés avec 4.7.2 ou moins : y installer .NET Framework 4.8 (Windows Update / WSUS,
   KB4486153), sinon Windows propose de le télécharger au lancement.
   Copier tout le dossier : `ErrorChecker.exe`, `ErrorChecker.exe.config` (redirections de versions,
   indispensable) et les DLL qui l'accompagnent. **Lors d'une mise à jour, ne pas écraser le
   `ErrorChecker.json` déjà configuré** (celui du dossier de compilation n'est qu'un exemple).
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

Si ça rame malgré tout : d'abord baisser la cadence, puis passer la qualité en « Économie » (couleurs très
légèrement réduites, texte toujours net). « Sans perte » : aucun JPEG, pixels exacts.

## Comment ça marche (et pourquoi c'est léger)

- **Transport** : un fichier journal par sens, lu au fil de l'eau via un handle ouvert (pas de liste de dossier,
  sujette aux caches SMB de 5 à 10 s), chiffré et authentifié (AES-CTR + HMAC-SHA256). La clé et le nom du dossier
  sont dérivés du code (PBKDF2) :
  rien de secret n'est écrit sur le partage.
- **Image** : seuls les pixels modifiés partent (rectangle serré dans les tuiles 64×64 touchées) ; un défilement est
  envoyé comme « recopier ces lignes » ; le texte et l'interface (peu de couleurs) passent par une palette **sans
  perte** compressée en Deflate, seules les colonnes riches (photo, icônes) en JPEG ; une zone envoyée en JPEG puis
  immobile est renvoyée nette après 0,7 s ; encodage et décodage en parallèle.

  Mesuré sur 16 captures réelles (Excel, éditeurs VBA/SQL, dialogues, Outlook, web, fonds photo ; JPEG d'ImageSharp
  en remplacement de GDI+) : image complète ≈ 65 Ko en moyenne, saisie dans une cellule ≈ 320 octets,
  défilement de 20 px ≈ 3 Ko.
- **Réactivité** : capture rapprochée juste après un clic ou une touche du dépanneur ; envoi immédiat des actions.

## Limites

- Ctrl+Alt+Suppr, l'écran UAC et les applications lancées en administrateur ne peuvent pas être pilotés.
- Le lien `errorchecker:` doit être cliquable dans Outlook (classique) ; sinon, taper le code.
- Une session = un dépanneur ; pour recommencer, l'utilisateur refait une demande.

## Tests

```
dotnet test tests/ErrorChecker.Tests -f net48
```
Les tests tournent sur .NET Framework 4.8 (la cible de l'application) et sur .NET 6 : protocole, canal fichier,
chiffrement, diff, défilement, codec palette, et simulation complète vérifiant que l'écran du dépanneur reste
identique à celui de l'utilisateur. La cible net6.0 (`-f net6.0`) demande le runtime .NET 6.
Sous Linux, la cible net48 s'exécute avec Mono :
`mono ~/.nuget/packages/xunit.runner.console/2.5.0/tools/net481/xunit.console.exe tests/ErrorChecker.Tests/bin/Debug/net48/ErrorChecker.Tests.dll`
