using System.Diagnostics;
using System.IO;

namespace VideoSplitter.Services;

public static class ExternalPlayerLauncher
{
    public static bool TryOpenInDefaultPlayer(string? filePath, IAppLogger logger, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            errorMessage = "Le fichier vidéo à ouvrir est introuvable.";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(filePath)
            {
                UseShellExecute = true
            });
            logger.Info($"Ouverture de la video dans le lecteur externe par defaut : {filePath}");
            return true;
        }
        catch (Exception ex)
        {
            logger.Warn($"Impossible d'ouvrir la video dans le lecteur externe ({filePath}) : {ex.Message}");
            errorMessage = $"Impossible d'ouvrir le lecteur vidéo par défaut de Windows.\n\nDétail : {ex.Message}";
            return false;
        }
    }
}
