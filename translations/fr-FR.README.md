# Traduction française de Moonfin

Ce dossier conserve la traduction française des pages d’administration du plugin.

Le fichier `fr-FR.patch` est la sauvegarde des modifications de traduction appliquées aux pages Jellyfin et Emby. Après une mise à jour du plugin, réappliquer ce patch depuis la racine du dépôt avec :

```bash
git apply translations/fr-FR.patch
```

Si le dépôt a évolué entre-temps, Git peut signaler des conflits. Dans ce cas, il faut conserver les nouvelles fonctionnalités du plugin et réappliquer uniquement les lignes de traduction concernées.

Ne pas compiler automatiquement après une mise à jour : vérifier d’abord le patch, puis compiler seulement après validation.
