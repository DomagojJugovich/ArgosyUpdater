# ArgosyUpdater – deployment na radne stanice (GPO)

| | |
|---|---|
| Verzija | 0.1 |
| Datum | 2026-09-29 |
| Autori | Domagoj Jugović, Claude |
| Status | Draft |
| Tehnologije | Group Policy Preferences (Immediate Task), Windows PowerShell 5.1, robocopy |
| Projekti | ArgosyUpdater |

## Koncept

`\\bepo\ArgosyUpdater` je izvor. GPO na svakoj stanici pokreće `Deploy-ArgosyUpdater.ps1` (kao SYSTEM) pri svakom Group Policy refreshu: pri bootu, pa svakih ~90 min.

| Korak | Što radi |
|---|---|
| robocopy `/E` share → `C:\Program Files\ArgosyUpdater_1_0` | Kopira samo promijenjene fileove i nikad ne briše. Prazan ili nedostupan share ne uklanja instalaciju (exit 2). |
| `C:\ProgramData\ArgosyWatcher` | Folder se napravi ako ne postoji i dobije `BUILTIN\Users: Modify`, da svaki korisnik PC-a može osvježiti running copy. |
| Startup i desktop shortcut | Isti nazivi kao kod `ArgosyUpdater.exe install`, napravi ih samo ako ih nema. |

Fileovi u Program Files nisu zaključani, jer updater radi iz kopije u `ProgramData`. Nova verzija se pokrene pri sljedećem loginu, ili odmah kad se na shareu osvježi `LastWriteTime` na `\\bepo\ARGOSY\_scripts\_aw_command.txt` (`RESTART`).

Zašto ne GPP **Files**: s akcijom **Update** postojeći file dobije samo nove atribute, sadržaj se ne kopira, pa nova verzija nikad ne bi stigla. **Replace** kopira sve (~10 MB) na svaki refresh svake stanice. Ni jedno ni drugo ne rješava prava na `ProgramData` i shortcut.

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
| 0 | OK (robocopy 0–7) |
| 1 | robocopy ≥ 8 ili druga greška |
| 2 | share nema `ArgosyUpdater.exe`, ništa nije dirano |

## Reference

- [Working with Windows Settings Preference Items (Files extension, actions)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/dn789188(v=ws.11))
- [Control Panel Settings preference items (Scheduled Tasks)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/dn789200(v=ws.11))
- [about_Execution_Policies](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_execution_policies)
- [robocopy](https://learn.microsoft.com/windows-server/administration/windows-commands/robocopy)
