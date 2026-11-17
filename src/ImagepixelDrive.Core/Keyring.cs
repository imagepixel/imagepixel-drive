using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using System.Text;

namespace ImagepixelDrive.Core;

/// <summary>
/// Keeps secrets in the keyring of the operating system, through a program that is
/// part of it.
/// </summary>
/// <remarks>
/// A secret is stored as text made of letters and digits only. That way it needs no
/// quoting, the program gives it back unchanged whatever characters it contains, and
/// it can be handed over through the input of the program instead of as an argument,
/// which other programs could read from the list of processes.
/// </remarks>
public abstract class KeyringSecretStore : ISecretStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, string?> _known = [];

    protected KeyringSecretStore(string service)
    {
        RequirePlainName(service);
        Service = service;
    }

    /// <summary>The name the secrets of the application are filed under.</summary>
    protected string Service { get; }

    public string? Get(string name)
    {
        RequirePlainName(name);
        lock (_lock)
        {
            // asking the keyring starts a program, so every secret is only read once
            if (!_known.TryGetValue(name, out var value))
            {
                value = Read(name) is { } text ? SecretText.Decode(text.Trim()) : null;
                _known[name] = value;
            }

            return value;
        }
    }

    public void Set(string name, string? value)
    {
        RequirePlainName(name);
        lock (_lock)
        {
            _known.Remove(name);
            if (value is null)
            {
                Delete(name);
            }
            else
            {
                var problem = Write(name, SecretText.Encode(value));

                // what counts is that the keyring gives the secret back
                if ((Read(name) is { } text ? SecretText.Decode(text.Trim()) : null) != value)
                {
                    throw new SecurityException($"The keyring could not be written: {problem.Trim()}");
                }
            }

            _known[name] = value;
        }
    }

    /// <summary>Returns the stored text, or null if there is none under this name.</summary>
    protected abstract string? Read(string name);

    /// <summary>Stores the text. Returns what the program had to say, to explain a failure.</summary>
    protected abstract string Write(string name, string text);

    protected abstract void Delete(string name);

    // Names become part of a command, so they must not need quoting.
    private static void RequirePlainName(string name)
    {
        if (name.Length == 0 || name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_')))
        {
            throw new ArgumentException($"\"{name}\" cannot be used as a name in the keyring.", nameof(name));
        }
    }

    protected static (int ExitCode, string Output, string Error) Run(string program, string[] arguments, string? input = null)
    {
        var startInfo = new ProcessStartInfo(program)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new SecurityException($"{program} could not be started.");
        }
        catch (Win32Exception e)
        {
            throw new SecurityException($"The keyring program {program} is missing: {e.Message}");
        }

        using (process)
        {
            if (input is not null)
            {
                process.StandardInput.Write(input);
            }

            process.StandardInput.Close();
            var error = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output, error.GetAwaiter().GetResult());
        }
    }
}

/// <summary>macOS: the keychain of the user, through the "security" program.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacKeychainSecretStore(string service = MacKeychainSecretStore.DefaultService)
    : KeyringSecretStore(service)
{
    public const string DefaultService = "de.imagepixel.drive";

    private const string SecurityPath = "/usr/bin/security";
    private const int ExitItemNotFound = 44;

    // "security" reads a command of at most this many characters from its input
    private const int MaxCommandLength = 4000;

    protected override string? Read(string name)
    {
        var (exitCode, output, error) = Run(SecurityPath, ["find-generic-password", "-a", name, "-s", Service, "-w"]);
        return exitCode switch
        {
            0 => output,
            ExitItemNotFound => null,
            _ => throw new SecurityException($"The keychain could not be read: {error.Trim()}"),
        };
    }

    protected override string Write(string name, string text)
    {
        // -U replaces an entry that exists already
        var command = $"add-generic-password -U -a {name} -s {Service} -w {text}\n";
        if (command.Length > MaxCommandLength)
        {
            throw new SecurityException("The secret is too long for the keychain.");
        }

        return Run(SecurityPath, ["-i"], command).Error;
    }

    protected override void Delete(string name)
    {
        var (exitCode, _, error) = Run(SecurityPath, ["delete-generic-password", "-a", name, "-s", Service]);
        if (exitCode is not (0 or ExitItemNotFound))
        {
            throw new SecurityException($"The keychain entry could not be removed: {error.Trim()}");
        }
    }
}

/// <summary>Writes a secret as text made of letters and digits only, and reads it back.</summary>
public static class SecretText
{
    // tells the text apart from anything else that may be found under the same name
    private const string Prefix = "ipd1";

    public static string Encode(string secret) => Prefix + Convert.ToHexString(Encoding.UTF8.GetBytes(secret));

    /// <summary>Returns null for text that <see cref="Encode"/> did not write.</summary>
    public static string? Decode(string text)
    {
        if (!text.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(Convert.FromHexString(text.AsSpan(Prefix.Length)));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
