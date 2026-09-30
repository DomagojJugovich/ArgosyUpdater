# ArgosyUpdater – deployment na radne stanice (GPO)

| | |
|---|---|
| Verzija | 0.1 |
| Datum | 2026-09-29 |
| Autori | Domagoj Jugović, Claude |
| Status | Draft |
| Tehnologije | Group Policy Preferences (Immediate Task), Windows PowerShell 5.1, `ArgosyUpdater.exe install`, robocopy /L |
| Projekti | ArgosyUpdater |

## Koncept

`\\bepo\ArgosyUpdater` je izvor. GPO na svakoj stanici pokreće `Deploy-ArgosyUpdater.ps1` (kao SYSTEM) pri svakom Group Policy refreshu: pri bootu, pa svakih ~90 min.

Sav posao radi `ArgosyUpdater.exe install` sa sharea, isto kao `_ArgosyUpdaterInstall.bat`. Kod za kopiranje, prava i shortcute (startup i common desktop) tako je na jednom mjestu, u `InstallApp`. Skripta samo odlučuje treba li install. `InstallApp` bezuvjetno kopira sve fileove, pa bi bez te provjere svaki GP refresh povukao ~10 MB.

| Uvjet | Posljedica |
|---|---|
| Nema `ArgosyUpdater.exe` u Program Files ili fali startup/desktop shortcut | install |
| `robocopy /L` share → Program Files (samo gornja razina, bez same skripte) nađe novi ili noviji file | install |
| Ništa od navedenog | ništa, exit 0 |
| Share nema `ArgosyUpdater.exe` | ništa, exit 2 |

Install mora vratiti izlazni kod 5, inače skripta završi s exit 1.

Prava koja install postavlja (od verzije s ovom izmjenom):
- **`C:\Program Files\ArgosyUpdater_1_0`:** nema dodatnih prava, samo naslijeđena, dakle korisnici samo čitaju. Updater tamo ništa ne piše. `Everyone: FullControl`, koji su dodavale starije verzije, uklanja se, jer je svakom korisniku omogućavao zamjenu exe-a koji se pokreće pri loginu drugih korisnika.
- **`C:\ProgramData\ArgosyWatcher`:** `BUILTIN\Users: Modify` (preko SID-a), nasljeđuje se na podfoldere i fileove, umjesto `Everyone: FullControl`. Svaki korisnik PC-a može osvježiti running copy, postavke i logove.

Fileovi u Program Files nisu zaključani, jer updater radi iz kopije u `ProgramData`. Nova verzija se pokrene pri sljedećem loginu, ili odmah kad se na shareu osvježi `LastWriteTime` na `\\bepo\ARGOSY\_scripts\_aw_command.txt` (`RESTART`).

Zašto ne GPP **Files**: s akcijom **Update** postojeći file dobije samo nove atribute, sadržaj se ne kopira, pa nova verzija nikad ne bi stigla. **Replace** kopira sve (~10 MB) na svaki refresh svake stanice. Ni jedno ni drugo ne radi shortcute ni prava, dok ih install radi.

## GPO (GPMC)

*Computer Configuration → Preferences → Control Panel Settings → Scheduled Tasks → New → Immediate Task (At least Windows 7)*

| Tab | Postavka |
|---|---|
| General | Name `ArgosyUpdater Deploy`, user `NT AUTHORITY\System`, *Run whether user is logged on or not*, *Run with highest privileges*, Configure for Windows 10 |
| Actions | Start a program: `powershell.exe`, arguments: `-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "\\bepo\ArgosyUpdater\Deploy-ArgosyUpdater.ps1"` |
| Settings | *Stop the task if it runs longer than* 30 minutes |
| Common | **bez** *Apply once and do not reapply*, jer task treba raditi pri svakom refreshu |

GPO se linka na OU s radnim stanicama. Terminal servere (TSPLUS) treba isključiti ili posebno testirati, jer više sesija na istom stroju pokreće isti updater.

## Preduvjeti

- **Pravo čitanja za račune računala:** na shareu i na NTFS-u `\\bepo\ArgosyUpdater` čitati moraju moći računi računala (Domain Computers ili Authenticated Users), jer task radi kao SYSTEM.
- **Pravo pisanja samo za admine:** skripta i exe s tog sharea izvršavaju se kao SYSTEM na svim stanicama.
- **Execution policy:** `MachinePolicy` iz GPO-a (sada `RemoteSigned`) nadjačava `-ExecutionPolicy Bypass`. Nepotpisana skripta s `\\bepo` (Intranet zona) prolazi s `RemoteSigned`. S FQDN putanjom (`\\bepo.du.laus.hr\...`) mogla bi biti blokirana. Najsigurnije je potpisati skriptu internim code signing certifikatom. Na jednoj stanici testiraj prije širenja.

## Provjera i rollback

- **Na stanici:** log je u `C:\Windows\Temp\ArgosyUpdater_Deploy.log`. Ručni dry-run (kao admin):

  ```powershell
  powershell.exe -NoProfile -ExecutionPolicy Bypass -File \\bepo\ArgosyUpdater\Deploy-ArgosyUpdater.ps1 -WhatIf
  ```

- **Po stanicama:** verzija je u `dbo.ArgosyUpdaterMachines.ArgosyUpdaterVersion`.
- **Rollback:** isključi ili unlinkaj GPO. Instalirani fileovi ostaju. Za povratak na staru verziju stavi stari build na share, pa osvježi `_aw_command.txt`.

## Izlazni kodovi skripte

| Kod | Značenje |
|---|---|
| 0 | ažurno, ili install uspješan (exit 5) |
| 1 | install nije vratio 5, robocopy `/L` ≥ 8 ili druga greška |
| 2 | share nema `ArgosyUpdater.exe`, ništa nije dirano |

## Reference

- [Working with Windows Settings Preference Items (Files extension, actions)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/dn789188(v=ws.11))
- [Control Panel Settings preference items (Scheduled Tasks)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/dn789200(v=ws.11))
- [about_Execution_Policies](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_execution_policies)
- [robocopy](https://learn.microsoft.com/windows-server/administration/windows-commands/robocopy)
