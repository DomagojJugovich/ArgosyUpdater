# ArgosyUpdater – deployment na radne stanice (GPO)

| | |
|---|---|
| Verzija | 0.2 |
| Datum | 2026-09-30 |
| Autori | Domagoj Jugović, Claude |
| Status | Draft |
| Tehnologije | Group Policy Preferences (Immediate Task), `ArgosyUpdater.exe install` |
| Projekti | ArgosyUpdater |

## Koncept

`\\bepo\ArgosyUpdater` je izvor. GPO na svakoj stanici pri svakom Group Policy refreshu (pri bootu, pa svakih ~90 min) pokreće `\\bepo\ArgosyUpdater\ArgosyUpdater.exe install`, kao SYSTEM. To je ista komanda koju pokreće `_ArgosyUpdaterInstall.bat` za ručnu instalaciju. Kopiranje, prava i shortcuti su na jednom mjestu, u `InstallApp`.

Nema PowerShell skripte, jer execution policy iz GPO-a (`MachinePolicy`, npr. `AllSigned`) nadjačava `-ExecutionPolicy Bypass` i blokira nepotpisane skripte. Na dio stanica to se i dogodilo. Execution policy se na exe ne odnosi.

Install je idempotentan i ne košta ništa kad nema promjena:

| Korak | Ponašanje |
|---|---|
| Fileovi sharea → `C:\Program Files\ArgosyUpdater_1_0` | Kao robocopy mirror, samo gornja razina: kopiraju se fileovi koji fale ili kojima se razlikuje veličina ili `LastWriteTime`, a brišu fileovi kojih više nema na shareu. Podfolderi se ne diraju. |
| Prava na `C:\Program Files\ArgosyUpdater_1_0` | Bez dodatnih prava, korisnici samo čitaju. Uklanja se `Everyone: FullControl` koji su dodavale starije verzije. |
| Prava na `C:\ProgramData\ArgosyWatcher` | `BUILTIN\Users: Modify` (preko SID-a, nasljeđuje se) umjesto `Everyone: FullControl`, tako da svaki korisnik PC-a može osvježiti running copy. |
| Startup i common desktop shortcut | Napravi se samo ako ne postoji ili pokazuje drugdje. |
| Log | `C:\Windows\Temp\ArgosyUpdater_Install.log`: verzija, broj kopiranih i obrisanih fileova, broj novih shortcuta, greške. |

Build se na share kopira u cijelosti, ne file po file. Ako GP refresh naiđe dok neki file na shareu privremeno fali, install ga obriše u Program Files, a vrati ga sljedeći refresh.

Fileovi u Program Files nisu zaključani, jer updater radi iz kopije u `ProgramData`. Nova verzija se pokrene pri sljedećem loginu, ili odmah kad se na shareu osvježi `LastWriteTime` na `\\bepo\ARGOSY\_scripts\_aw_command.txt` (`RESTART`).

Zašto ne GPP **Files**: s akcijom **Update** postojeći file dobije samo nove atribute, sadržaj se ne kopira, pa nova verzija nikad ne bi stigla. **Replace** kopira sve (~10 MB) na svaki refresh svake stanice. Ni jedno ni drugo ne radi shortcute ni prava.

## GPO (GPMC)

*Computer Configuration → Preferences → Control Panel Settings → Scheduled Tasks → New → Immediate Task (At least Windows 7)*

| Tab | Postavka |
|---|---|
| General | Name `ArgosyUpdater Install`, user `NT AUTHORITY\System`, *Run whether user is logged on or not*, *Run with highest privileges*, Configure for Windows 10 |
| Actions | Start a program: `\\bepo\ArgosyUpdater\ArgosyUpdater.exe`, arguments `install` |
| Settings | *Stop the task if it runs longer than* 30 minutes |
| Common | **bez** *Apply once and do not reapply*, jer task treba raditi pri svakom refreshu |

GPO se linka na OU s radnim stanicama. Terminal servere (TSPLUS) treba isključiti ili posebno testirati, jer više sesija na istom stroju pokreće isti updater.

## Preduvjeti

- **Pravo čitanja za račune računala:** na shareu i na NTFS-u `\\bepo\ArgosyUpdater` čitati moraju moći računi računala (Domain Computers ili Authenticated Users), jer task radi kao SYSTEM.
- **Pravo pisanja samo za admine:** exe s tog sharea izvršava se kao SYSTEM na svim stanicama.
- **AppLocker / SRP:** ako stanice imaju pravila za exe-ove s mrežnih putanja, `\\bepo\ArgosyUpdater\ArgosyUpdater.exe` mora biti dopušten.

## Provjera i rollback

- **Na stanici:** log je u `C:\Windows\Temp\ArgosyUpdater_Install.log`. Zadnji rezultat taska vidi se u Task Scheduleru (5 = OK). Ručno, kao admin: `\\bepo\ArgosyUpdater\_ArgosyUpdaterInstall.bat`.
- **Po stanicama:** verzija je u `dbo.ArgosyUpdaterMachines.ArgosyUpdaterVersion`.
- **Rollback:** isključi ili unlinkaj GPO. Instalirani fileovi ostaju. Za povratak na staru verziju stavi stari build na share, pa osvježi `_aw_command.txt`.

## Izlazni kodovi `ArgosyUpdater.exe install`

| Kod | Značenje |
|---|---|
| 5 | OK, i kad nije bilo promjena |
| -1 | greška, detalji u logu |

## Povijest

| Verzija | Datum | Promjena |
|---|---|---|
| 0.1 | 2026-09-29 | GPO Immediate Task s PowerShell skriptom (robocopy) |
| 0.2 | 2026-09-30 | Bez skripte (blokirana execution policyjem), task izravno pokreće `ArgosyUpdater.exe install`, install je idempotentan |

## Reference

- [Working with Windows Settings Preference Items (Files extension, actions)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/dn789188(v=ws.11))
- [Control Panel Settings preference items (Scheduled Tasks)](https://learn.microsoft.com/en-us/previous-versions/windows/it-pro/windows-server-2012-r2-and-2012/dn789200(v=ws.11))
- [about_Execution_Policies](https://learn.microsoft.com/powershell/module/microsoft.powershell.core/about/about_execution_policies)
