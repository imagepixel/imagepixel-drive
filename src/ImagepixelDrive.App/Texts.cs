using System;
using System.Globalization;
using ImagepixelDrive.Core;
using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.App;

/// <summary>Turns values and errors into the German texts shown to the user.</summary>
public static class Texts
{
    public const string ProductName = "Imagepixel Drive";

    /// <summary>
    /// The version of the notes on use and liability, which is the date they were last
    /// changed. The full text is the file NUTZUNGSHINWEISE.md. The user confirms the
    /// notes once at the first start; changing this value asks everyone once more.
    /// </summary>
    public const string TermsVersion = "2026-11-17";

    /// <summary>The short form of the notes, shown once at the first start.</summary>
    public const string TermsSoftware =
        "Imagepixel Drive ist kostenlose, quelloffene Software und wird ohne Gewährleistung bereitgestellt. "
        + "Für Fehler des Programms und für den Verlust von Daten haftet Imagepixel nur bei Vorsatz und grober Fahrlässigkeit.";

    public const string TermsStorage =
        "Kein Speicher ist vor jedem Ausfall sicher, auch Imagepixel Storage nicht. Ein Laufwerk ist kein Backup: "
        + "Bewahre von wichtigen Dateien immer eine zweite Kopie an einem anderen Ort auf.";

    public const string TermsPassword =
        "Dein Passwort kennst nur du. Ohne das Passwort kommt niemand mehr an deine Dateien, auch Imagepixel nicht.";

    public const string TermsConfirm = "Ich habe die Nutzungshinweise gelesen und bin damit einverstanden.";

    /// <summary>What the info page says about the notes the user confirmed.</summary>
    public const string TermsReminder =
        "Imagepixel Drive ist kostenlose, quelloffene Software ohne Gewährleistung. Die Hinweise zur Nutzung und Haftung, "
        + "die du beim ersten Start bestätigt hast, kannst du hier nachlesen.";

    public const string PreviewNote =
        "Das ist eine Vorabversion. Sie wurde noch nicht im Alltag erprobt. Lege wichtige Dateien nicht nur hier ab.";

    public const string PasswordWarning =
        "Wenn du das Passwort verlierst, kommt niemand mehr an deine Dateien – auch Imagepixel nicht. Es lässt sich nicht "
        + "zurücksetzen. Schreib es auf und bewahre es sicher auf, zum Beispiel in einem Passwort-Manager.";

    public const string EmergencyAccess =
        "Dein Laufwerk ist mit dem frei erhältlichen Programm rclone verschlüsselt, in dessen Standardformat. "
        + "Mit rclone, deinen Zugangsdaten und deinem Passwort kommst du auch ohne Imagepixel Drive an deine Dateien. "
        + "Wie das geht, steht in der Beschreibung auf GitHub.";

    public const string OfflineHint =
        "Ordner, die du hier auswählst, liegen zusätzlich auf diesem Rechner. Du kannst darin auch ohne Internet arbeiten; "
        + "Änderungen werden abgeglichen, sobald die Verbindung wieder da ist.";

    public const string Unreachable =
        "Der Speicher ist gerade nicht erreichbar. Deine Offline-Ordner bleiben benutzbar; das Laufwerk selbst braucht eine Verbindung.";

    /// <summary>What the program is called that shows folders and files.</summary>
    public static string FileManager => OperatingSystem.IsWindows() ? "Explorer" : "Finder";

    /// <summary>Where the application waits while its window is closed.</summary>
    public static string TrayPlace => OperatingSystem.IsMacOS() ? "in der Menüleiste" : "im Infobereich";

    public static string ShowDrive => $"Im {FileManager} anzeigen";

    public static string KeepsRunning =>
        $"Wenn du dieses Fenster schließt, läuft Imagepixel Drive {TrayPlace} weiter. Dort beendest du es auch.";

    /// <summary>An example of a place for a drive on a disk of the user.</summary>
    public static string LocalPathExample =>
        OperatingSystem.IsWindows() ? @"z. B. E:\ oder C:\Users\…\OneDrive" : "z. B. /Volumes/Festplatte";

    /// <summary>Says that the drive can be used. Only Windows gives it a letter worth naming.</summary>
    public static string DriveReady(string mountpoint) =>
        OperatingSystem.IsWindows() ? $"Laufwerk {mountpoint} ist bereit" : "Dein Laufwerk ist bereit";

    public static string DriveWhere(string root) => OperatingSystem.IsWindows()
        ? $"Du findest es im Explorer unter „Dieser PC“ als „{ProductName}“."
        : $"Du findest es im Finder unter {root}.";

    /// <summary>Where a drive lies when the user chose a folder that holds other things.</summary>
    public static string DriveFolder(string folder) => $"Das Laufwerk liegt dann in: {folder}";

    /// <summary>What it means to put a drive into the folder of a cloud service.</summary>
    public static string CloudPlace(string service) =>
        $"Dieser Ordner gehört zu {service}. Deine Dateien werden auf diesem Rechner verschlüsselt und dann von {service} "
        + $"hochgeladen. {service} bekommt nur verschlüsselte Namen und Inhalte zu sehen.";

    /// <summary>A number of files, for example "1 Datei" or "14 Dateien".</summary>
    public static string Files(long count) => count == 1 ? "1 Datei" : $"{count} Dateien";

    public static string PendingUploads(int count) => count switch
    {
        0 => "Alles gespeichert.",
        1 => "1 Datei wird noch gespeichert …",
        _ => $"{count} Dateien werden noch gespeichert …",
    };

    /// <summary>Said before a drive is disconnected while files are still on their way.</summary>
    public static string DisconnectLoses(int count) => count == 1
        ? "1 Datei ist noch nicht gespeichert. Was beim Trennen noch nicht gespeichert ist, geht verloren."
        : $"{count} Dateien sind noch nicht gespeichert. Was beim Trennen noch nicht gespeichert ist, geht verloren.";

    /// <summary>What starting at login means on this system.</summary>
    public static string StartAtLoginHint =>
        $"Damit dein Laufwerk ohne dein Zutun da ist, kann das Programm bei jeder Anmeldung starten. Es wartet dann {TrayPlace}.";

    public static string StartsAtLoginNow =>
        $"{ProductName} startet jetzt bei jeder Anmeldung von selbst und wartet {TrayPlace}.";

    public const string StartAtLoginNote =
        "Imagepixel Drive startet noch nicht von selbst. Nach einem Neustart ist dein Laufwerk erst wieder da, wenn du das Programm öffnest.";

    /// <summary>macOS: why the application cannot start at login from where it currently lies.</summary>
    public const string MoveToApplications =
        "Bitte ziehe Imagepixel Drive zuerst in den Ordner „Programme“ und öffne es von dort. "
        + "Erst dann kann es bei der Anmeldung von selbst starten.";

    /// <summary>How much of the booked storage is used, for example "12,4 GB von 100 GB belegt (12 %)".</summary>
    public static string StorageUse(StorageStatus status) =>
        $"{Bytes(status.UsedBytes)} von {Bytes(status.BookedBytes)} belegt ({Percent(status.Fraction)})";

    /// <summary>The warning shown from 80 % on. The drive keeps working when the storage is full.</summary>
    public static string? StorageWarning(StorageStatus? status) => status switch
    {
        { IsFull: true } =>
            $"Dein gebuchter Speicher ist voll ({Percent(status.Fraction)}). Dein Laufwerk läuft weiter. Imagepixel meldet sich bei dir wegen der nächsten Größe.",
        { IsWarning: true } =>
            $"Dein Speicher ist zu {Percent(status.Fraction)} belegt. Dein Laufwerk läuft weiter. Imagepixel meldet sich bei dir wegen der nächsten Größe.",
        _ => null,
    };

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static string Bytes(long bytes)
    {
        string[] units = ["Bytes", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[0]}"
            : string.Create(German, $"{value:0.#} {units[unit]}");
    }

    /// <summary>Formats a share as a whole percentage, for example "86 %".</summary>
    public static string Percent(double fraction) =>
        string.Create(German, $"{Math.Round(fraction * 100):0} %");

    /// <summary>Formats a point in time relative to today, for example "heute, 15:30".</summary>
    public static string Day(DateTimeOffset time, DateTimeOffset now)
    {
        var local = time.ToLocalTime();
        var days = (now.ToLocalTime().Date - local.Date).Days;
        var clock = local.ToString("HH:mm", German);
        return days switch
        {
            0 => $"heute, {clock}",
            1 => $"gestern, {clock}",
            _ => local.ToString("dd.MM.yyyy, HH:mm", German),
        };
    }

    public static string StorageName(StorageSettings storage) => storage.Kind switch
    {
        StorageKind.ImagepixelStorage => $"Imagepixel Storage · {storage.Bucket}",
        _ => $"Eigener Speicherort · {storage.LocalPath}",
    };

    /// <summary>What a sync of an offline folder did, or why it stopped.</summary>
    public static string SyncStatus(SyncResult? result, DateTimeOffset? when, bool syncing, DateTimeOffset now)
    {
        if (syncing)
        {
            return "Wird abgeglichen …";
        }

        if (result is null)
        {
            return "Wartet auf den ersten Abgleich …";
        }

        return result.Outcome switch
        {
            SyncOutcome.Synced => "Abgeglichen" + (when is { } time ? $", {Day(time, now)}" : ""),
            SyncOutcome.Offline =>
                "Speicher nicht erreichbar. Der Ordner bleibt benutzbar; abgeglichen wird, sobald die Verbindung wieder da ist.",
            SyncOutcome.TooManyDeletes =>
                "Angehalten: In diesem Ordner wurden sehr viele Dateien gelöscht. Damit ein Versehen nicht auch die andere "
                + "Seite leert, wurde nichts übernommen.",
            SyncOutcome.AllFilesChanged =>
                "Angehalten: Alle Dateien erscheinen geändert, zum Beispiel nach einer Zeitumstellung. Es wurde nichts übernommen.",
            SyncOutcome.FolderMissing =>
                "Angehalten: Der Ordner fehlt auf diesem Rechner oder im Laufwerk. Es wurde nichts gelöscht.",
            SyncOutcome.NeedsRepair =>
                "Der Abgleich muss neu aufgesetzt werden. Dabei wird nichts gelöscht; zuvor gelöschte Dateien können wieder auftauchen.",
            _ => "Abgleich fehlgeschlagen" + (result.Errors.Count > 0 ? ": " + result.Errors[^1] : "."),
        };
    }

    /// <summary>Names the files that were kept twice because both sides changed them.</summary>
    public static string? Conflicts(SyncResult? result) => result is { Conflicts.Count: > 0 }
        ? (result.Conflicts.Count == 1 ? "Eine Datei wurde" : $"{result.Conflicts.Count} Dateien wurden")
          + " auf beiden Seiten geändert. Die neuere Fassung hat den Namen behalten, die andere liegt daneben: "
          + string.Join(", ", result.Conflicts)
        : null;

    /// <summary>What the button does that confirms a sync that stopped to ask, or null if there is nothing to confirm.</summary>
    public static (string Label, SyncMode Mode)? Confirmation(SyncResult? result) => result?.Outcome switch
    {
        SyncOutcome.TooManyDeletes => ("Löschungen übernehmen", SyncMode.AcceptAll),
        SyncOutcome.AllFilesChanged => ("Trotzdem abgleichen", SyncMode.AcceptAll),
        SyncOutcome.NeedsRepair => ("Neu aufsetzen", SyncMode.Repair),
        _ => null,
    };

    /// <summary>Why a folder cannot be used for what the user chose it for.</summary>
    public static string FolderRule(FolderRuleException error) => error.Reason switch
    {
        FolderRuleException.Rule.WholeDrive =>
            "Das ganze Laufwerk kann nicht offline gehalten werden. Bitte wähle einen Ordner darin aus.",
        FolderRuleException.Rule.NotOnDrive =>
            "Dieser Ordner liegt nicht auf dem Laufwerk. Bitte wähle einen Ordner auf dem Laufwerk aus.",
        FolderRuleException.Rule.OfflineAlready =>
            $"„{error.Folder}“ ist schon offline verfügbar, oder liegt in oder um den Offline-Ordner „{error.Other}“.",
        _ =>
            $"Dieser Ordner wird von Imagepixel Drive selbst benutzt („{error.Other}“). Bitte wähle einen anderen Ort.",
    };

    /// <summary>Explains an error in words a customer can act on.</summary>
    public static string Error(Exception error)
    {
        var message = error.Message;
        bool Has(string text) => message.Contains(text, StringComparison.OrdinalIgnoreCase);

        if (error is FolderRuleException rule)
        {
            return FolderRule(rule);
        }

        if (Has("InvalidAccessKeyId") || Has("SignatureDoesNotMatch") || Has("access key"))
        {
            return "Der Zugangsschlüssel oder der geheime Schlüssel stimmt nicht.";
        }

        if (Has("Access Denied") || Has("AccessDenied"))
        {
            return "Der Zugriff wurde verweigert. Bitte prüfe Bucket-Name und Zugangsschlüssel.";
        }

        if (Has("NoSuchBucket") || Has("bucket does not exist"))
        {
            return "Diesen Bucket gibt es nicht. Bitte prüfe den Bucket-Namen.";
        }

        if (Has("no such host") || Has("dial tcp") || Has("i/o timeout") || Has("connection refused") || Has("request send failed"))
        {
            return "Der Speicher ist nicht erreichbar. Besteht eine Internetverbindung?";
        }

        if (Has("winfsp") || Has("cgofuse"))
        {
            return "Zum Anzeigen des Laufwerks fehlt die Komponente WinFsp. Bitte installiere " + ProductName + " erneut.";
        }

        if (Has("No drive letter is free"))
        {
            return "Es ist kein Laufwerksbuchstabe mehr frei.";
        }

        if (Has("is not empty, so the drive cannot be shown"))
        {
            return "Das Laufwerk kann nicht angezeigt werden, weil der Ordner dafür nicht leer ist: "
                + (DriveMount.DefaultMountpoint() ?? "") + ". Bitte räume ihn weg und versuche es erneut.";
        }

        if (Has("closed unexpectedly"))
        {
            return "Das Laufwerk wurde unerwartet geschlossen.";
        }

        if (error is System.IO.DirectoryNotFoundException || Has("cannot find the path") || Has("no such file or directory"))
        {
            return "Der Ordner wurde nicht gefunden. Ist die Festplatte angeschlossen?";
        }

        if (error is UnauthorizedAccessException)
        {
            return "Auf diesen Ordner darf nicht zugegriffen werden. Bitte wähle einen anderen Ort.";
        }

        if (error is System.Security.SecurityException)
        {
            return "Der Schlüsselbund dieses Rechners ist nicht erreichbar, deshalb kann das Passwort nicht sicher abgelegt werden. " + message;
        }

        if (error is System.ComponentModel.Win32Exception || Has("rclone") && Has("not found"))
        {
            return "Das Programm rclone wurde nicht gefunden. Bitte installiere " + ProductName + " erneut.";
        }

        if (error is RcloneException)
        {
            return "Das hat nicht geklappt: " + message;
        }

        return message;
    }
}
