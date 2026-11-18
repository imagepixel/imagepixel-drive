# imagepixel-drive
S3 crypted drive

Imagepixel Drive shows an encrypted storage as a drive of your computer. Every file is
encrypted on your computer before it leaves it. It is the sister of
[Imagepixel Backup](https://github.com/imagepixel/imagepixel-backup).

A drive is not a backup: what you delete or overwrite on it is gone.

## What it does

**A drive for your files**

- The drive appears with a drive letter on Windows and as a folder on macOS. You work
  on it like on any disk.
- The names and the content of your files are encrypted with a password that only you know.
- The application stays in the notification area, on macOS in the menu bar, and keeps
  the drive there. It starts with the computer if you want it to: the setup program
  offers that on Windows, the overview and the info page on macOS.

**Two places to store it**

- *Imagepixel Storage*: the online storage of Imagepixel. You paste the setup code you
  were given, one line of text, and nothing else is needed to reach the storage. The
  overview shows how much of the storage you booked is used.
- *A folder or disk of your own*: for example a folder inside OneDrive, Google Drive,
  Dropbox or iCloud Drive, or a USB disk. The cloud service of your choice then carries
  the encrypted files into its cloud and sees nothing but encrypted names and content.
  Cloud folders found on the computer are offered with one click.

**Folders that work without a connection**

- With Imagepixel Storage, folders you choose are kept on the computer as well. You can
  read and change them without a network connection; the changes are carried over once
  the connection is back, in both directions.
- A file that was changed on both sides is kept twice: the newer version keeps its
  name, the other one is stored next to it as `name.Konflikt1.ext`.
- A sync stops and asks if more than half of the files of a folder were deleted, and
  stops if the folder has vanished on one side, so that a mistake or a missing disk
  does not empty the other side.

## Your password

The drive is encrypted with a password that you choose. It is not sent to Imagepixel
and nobody else knows it. If you lose it, nobody can read the drive any more, Imagepixel
included, and it cannot be reset. Write it down and keep it somewhere safe.

The names and the content of your files are encrypted. Their sizes, the times they
were changed and the number of files in a folder stay visible in the storage.

## No warranty

Imagepixel Drive is free, open-source software and comes without warranty. You use it
at your own risk. Imagepixel is liable for defects of the application and for lost
data only where German law does not allow liability to be excluded, above all in cases
of intent and gross negligence. No storage is safe from every failure, the one of
Imagepixel included, and a drive is not a backup: keep a second copy of what matters
somewhere else.

The full notes on use and liability are in
[NUTZUNGSHINWEISE.md](NUTZUNGSHINWEISE.md) (in German). The application shows their
short form once, at its first start, and asks you to confirm it.

## Reading a drive without this application

A drive is nothing but an rclone `crypt` remote with the standard settings. With
rclone from [rclone.org](https://rclone.org/downloads/), the access to your storage
and your password you reach your files even if this application no longer exists.

For a drive in Imagepixel Storage:

```
rclone config create ipstore s3 provider=Wasabi endpoint=s3.eu-central-2.wasabisys.com region=eu-central-2 access_key_id=YOUR-KEY-ID secret_access_key=YOUR-SECRET
rclone config create ipdrive crypt remote=ipstore:YOUR-BUCKET/drive password=YOUR-PASSWORD --obscure
rclone ls ipdrive:
rclone copy ipdrive: C:\Rescue
```

The bucket, the key and its secret are what your setup code holds. The text after
`IPD1-` is a JSON object in base64url notation with the fields `b` (bucket), `k` (key
id), `s` (secret) and `r` (region), and `p` if the drive lies in another folder of the
bucket than `drive`.

For a drive in a folder of your own, name the folder instead of the storage:

```
rclone config create ipdrive crypt remote="C:\Users\you\OneDrive\Imagepixel Drive" password=YOUR-PASSWORD --obscure
rclone ls ipdrive:
```

A drive is not a Cryptomator vault; the two cannot open each other's files.

## Status

Early development, version 0.1.1. Nothing is signed yet, and nobody has used the
application in daily work.

The **Windows** version has a setup program. Its tests show a real drive and run real
syncs, including a lost connection. Drive and offline folders were also tried against
a bucket of Imagepixel Storage, with the program for the command line. The setup
program was run on one computer with Windows 11, and the installed application was
walked through there: setting up a drive in a folder, writing and reading files,
starting in the background, ending it abruptly, disconnecting. The step of the setup
program that installs WinFsp did not run, because WinFsp was on that computer
already, and removing the application was not tried.

The **macOS** version is a **beta**. It shows the drive through the NFS client that is
part of macOS, so nothing has to be installed and no kernel extension is involved. It
was tried on one Mac with an Intel chip and macOS 12: setting up a drive, reading,
writing, saving from applications, copying files together with their extended
attributes, renaming, an offline folder, and ending the application abruptly all
worked. It has not been used by hand in the Finder or with office programs for any
length of time yet, which is why it stays a beta. What comes with a drive of this kind:

- macOS cannot store extended attributes on it, such as the note that a file was
  downloaded from the internet. It keeps them in a companion file next to the file,
  named `._` followed by the name of the file. The Finder hides these files; Windows
  shows them. Offline folders leave them out.
- macOS asks once for every application whether it may access files on a network
  volume, because that is what the drive is to it.
- The Finder lists the drive under Locations as "localhost".
- rclone serves the drive to this computer only, but does not ask who connects. On a
  Mac that several people are logged in to at the same time, another one of them
  could reach the drive while it is shown.

The [workflow](.github/workflows/macos.yml) builds and tests this version on a Mac of
GitHub with an Apple chip and keeps the disk images as a download of the run.

Known limits:

- Without a connection the drive itself is of little use: it answers for folders whose
  list it still knows (one minute), accepts new files there and stores them later, and
  reports errors for everything else. Working without a connection is what offline
  folders are for.
- An offline folder is a folder of its own on the computer (`Imagepixel Drive Offline`
  in your profile), next to the drive. The same files are reachable at both places.
- Changes of other computers show up on the drive after a minute at the latest, in an
  offline folder with the next sync.
- An offline folder leaves out the files macOS writes by itself: `.DS_Store` and the
  companion files whose names start with `._`.
- Renaming a folder inside an offline folder transfers its files again.
- A drive in a cloud folder that two computers change at the same time: if the cloud
  service keeps both versions of a file under a name of its own, the drive does not
  show its copy, because that name is not an encrypted one. The copy stays in the
  cloud folder.

## Building

### Windows

Requirements: Windows 10 or 11 (64 bit) and the .NET 10 SDK.
[WinFsp](https://winfsp.dev) is needed to show the drive, and Inno Setup 6 to build
the setup program.

```powershell
# fetches the pinned rclone release, checks it and puts it into bin\rclone.exe
.\get-rclone.ps1

# runs the application
dotnet run --project src/ImagepixelDrive.App

# runs the tests
dotnet test

# builds artifacts\installer\ImagepixelDrive-Setup-<version>.exe
.\build-installer.ps1
```

The setup program brings everything a computer needs: the application with the .NET
runtime, rclone, and the installer of WinFsp, which it runs if WinFsp is missing. It
shows no licence page; the application asks for the notes on use and liability to be
confirmed at its first start, on Windows and macOS alike.

### macOS (beta)

Requirements: macOS 12 or later and the .NET 10 SDK.

```bash
# fetches rclone for Macs with an Apple chip and with an Intel chip
./get-rclone.sh

# runs the application
dotnet run --project src/ImagepixelDrive.App

# runs the tests
dotnet test

# builds artifacts/macos/ImagepixelDrive-<version>-beta-mac-arm64.dmg and -beta-mac-x64.dmg
./build-macos.sh

# takes the application out of its disk image and checks that it starts
installer/macos/smoke-test.sh
```

A disk image holds the application with the .NET runtime and rclone. The application
carries an ad-hoc signature only.

### Tests

The tests run rclone against storages in the temp folder: a plain folder, and an S3
compatible storage that rclone itself serves on the computer, reached through a
connection the tests can cut. They also walk through the user interface without
showing a window; pictures of every screen are saved to `artifacts/screenshots`. The
tests that show a drive need WinFsp on Windows and are skipped without it.

`tools/ImagepixelDrive.Prototype` is a small program for the command line that shows a
drive and keeps folders offline without the user interface. Run it without arguments
for its options.

## Layout

| Path | Content |
|---|---|
| `src/ImagepixelDrive.Core` | Runs rclone: access and encryption, the drive, the sync of offline folders; settings and secrets |
| `src/ImagepixelDrive.App` | The desktop application (Avalonia) |
| `tools/ImagepixelDrive.Prototype` | `ipd-prototype`: drive and offline folders from the command line |
| `installer` | The script of the setup program for Windows, what the disk image for macOS is made of, and the script that draws the icon |
| `tests` | Tests for the core and a walkthrough of the user interface |

## How it works

Imagepixel Drive is built on [rclone](https://rclone.org), which it uses as released,
without changes.

| Part | Done by |
|---|---|
| Encryption | The `crypt` backend of rclone with its standard settings and one password |
| Drive on Windows | `rclone mount` through [WinFsp](https://winfsp.dev) |
| Drive on macOS | `rclone nfsmount` through the NFS client of macOS, with the mount options `locallocks`, `nonegnamecache` and `actimeo=3` |
| Offline folders | `rclone bisync` between a folder on the computer and the folder in the drive |

Files you save or open on the drive pass through a cache folder in your user profile.
They lie there as they are, not encrypted, like any other file on your computer.
rclone stores a file a few seconds after it was closed. If the application or the
computer stops before that, the file waits in the cache folder and is stored when the
drive is shown the next time. Disconnecting a drive removes its cache folder, with
the files that have not been stored by then; the application says so if there are any.

Both sides of an offline folder carry a small file named `.imagepixel-offline`; a
sync only runs while both have it. The password and the access key are kept with the
Data Protection API on Windows and in the keychain on macOS, and reach rclone through
the environment of its process, never through a command line or a file.

## Contributing

This repository does not take contributions from outside: pull requests are limited to
collaborators and issues are turned off. You are free to use and adapt the code under the
terms of the license.

## License

Imagepixel Drive is licensed under the [BSD 2-Clause License](LICENSE).
The components it includes are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

The drive is shown with WinFsp - Windows File System Proxy, Copyright (C) Bill Zissimopoulos,
<https://github.com/winfsp/winfsp>.
