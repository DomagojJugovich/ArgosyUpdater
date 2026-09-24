# ArgosyUpdater delta sync (EXEDIR)

| | |
|---|---|
| Verzija | 0.1 |
| Datum | 2026-09-23 |
| Autori | Domagoj Jugović, Claude |
| Status | Draft |
| Tehnologije | .NET Framework 4.8, Octopus.Octodiff 2.0.549 (Apache-2.0), GZip, SHA256 |
| Projekti | ArgosyUpdater, ArgosyDeltaBuilder |

## Problem

`EXEDIR` drži verzije jednu pored druge (`Argosy_yyyy_MM_dd__HH_mm_ss`, ~1.1 GB, ~900 fileova). Obični file sync povlači **sve** verzije koje PC nema. PC koji je bio offline 5 buildova povuče ~5.5 GB. Build mijenja timestamp gotovo svih fileova, pa usporedba po timestampu ne pomaže.

Izmjereno na `\\bepo\ARGOSY\EXEDIR` (SHA256, stvarni sadržaj):

| Par | Identično | Različito |
|---|---|---|
| `09_02__12_04` -> `09_07__07_32` | 708 fileova / 843 MB | 215 / 332 MB |
| `09_07__07_32` -> `09_18__11_41` | 788 / 852 MB | 133 / 324 MB |

Octodiff + gzip na promijenjenim DLL-ovima: `WinFink.dll` 41.5 MB -> 4.1 MB, `WinBase.dll` 30.6 MB -> 2.2 MB (sam gzip: 8.6 / 8.0 MB).

## Koncept

**Server (`ArgosyDeltaBuilder`, Scheduled Task na bepo)**, idempotentan, svakih 5 min:

1. Verzija je *kompletna* ako ima `_READY` marker upisan **nakon** svega ostalog (tolerancija 2 s) **ili** se ništa u njoj (ni `CreationTime` ni `LastWriteTime`) nije mijenjalo `QuietMinutes` (10). `CreationTime` je bitan jer robocopy i Explorer zadržavaju `LastWriteTime` builda. Marker kopiran zajedno s folderom se ne računa, jer robocopy kopira fileove iz korijena prije podfoldera, pa takav marker vrijedi kao da ga nema.
   Promjena samo na direktorijima nakon markera mora mirovati `QuietMinutes`, pa tek onda verzija vrijedi kao gotova. Primjeri: Explorer stvori `Thumbs.db`, ili se folder upravo briše sa sharea. Tako takva promjena ne blokira verziju zauvijek, a verzija koja se briše ne objavljuje se.
   Promjena foldera se prepoznaje po popisu direktorija i fileova s veličinom, `LastWriteTime` i Hidden/System atributima, bez hashiranja. Promjena sadržaja koja zadrži i veličinu i `LastWriteTime` se ne vidi.
   Quiet period ne razlikuje gotov upload od prekinutog. Čim publish skripta upisuje `_READY` na kraju, uključi `"RequireReadyMarker": true`. Tada je marker jedini znak da je verzija gotova, a prekinuti upload se nikad ne objavi.
2. Za kompletnu verziju upisuje se manifest (svi fileovi + SHA256). Klijent **ne vidi verziju dok nema manifest**. Ako se folder verzije promijeni nakon manifesta (ponovni upload), manifest se preimenuje u `manifests\~<verzija>.json` (pending). Tako je skriven kao cilj, ali klijent po njemu i dalje prepoznaje svoju lokalnu kopiju. Kad je verzija opet kompletna, radi se rehash. Ako se sadržaj stvarno promijenio, brišu se njezine delte.
3. Za svake dvije uzastopne kompletne verzije radi se delta set. Za svaki file je jedno od: `Copy` (isti hash, prepoznaje i preimenovanja), `Patch` (octodiff delta, gzip) ili `Full` (gzip), ovisno što je manje.
4. Delta set se gradi u `~<par>` i rename-a se tek kad je gotov.
5. Retencija: delta set se čuva barem `RetentionDays` (90) i uvijek dok mu `From` verzija postoji na shareu. Tako PC koji je dugo bio offline može patchati i s verzije koja je već obrisana sa sharea.

**Klijent (`VersionSync` u ArgosyUpdateru)**:

1. Cilj je najnovija verzija na shareu koja ima manifest. Ostale verzije se **ne** skidaju.
2. Base je najnovija lokalna verzija do koje postoji delta lanac (BFS, najmanje koraka). Lanac se primjenjuje bez materijaliziranja međuverzija: drži se samo mapa `path -> lokalni file sa sadržajem`.
3. Od svih lokalnih verzija bira se lanac s najmanje podataka za skinuti. Ako je veći od 5% verzije, uspoređuje se s kopiranjem, pa se uzima jeftinije.
   Kod kopiranja se identični fileovi (po SHA256, i kad su premješteni) uzimaju iz najnovije lokalne verzije. Ostalo dolazi gzipano iz delta seta koji vodi u cilj (`Full`), a sirovo sa sharea samo kad toga nema.
   Hash svakog filea računa se tijekom kopiranja ili dekompresije. Octodiff izlaz je već provjeren (SHA1). Verifikacija zato ne čita fileove ponovno.
   Sav rad sa shareom i bazom (`CheckCommand`, `DirectoryCopy`, `VersionSync`, `DirectoryClean`, `UpdateDb`) ide na worker threadu, a tray ostaje responzivan i kad share nije dostupan. Za vrijeme synca:
   - `CHECK NOW` samo pokaže balloon "Sync in progress";
   - `SETTINGS` se odbija;
   - `EXIT` traži potvrdu.

   Neispravne postavke iz `SETTINGS` se ne spremaju.
   Hidden/System atributi fileova prenose se iz manifesta, a ReadOnly se uvijek skida.
4. Sve se gradi u `EXEDIR\~s_<verzija>`. Svaki file se provjerava prema manifestu (SHA256). Neispravan file se jednom ponovno kopira sa sharea. Tek nakon toga ide `Directory.Move` u `EXEDIR\<verzija>`.
5. `ArgosyBoot.ps1` pokreće samo `Argosy*` foldere, pa verziju koja se trenutno patcha nikad ne pokrene i korisnik dobije staru.
   Lokalna verzija novija od cilja, koja **postoji na shareu** bez normalnog manifesta (još se uploada, ponovno se uploada ili je builder još nije obradio), provjerava se: s pending manifestom po njemu, bez manifesta po popisu fileova i veličinama na shareu. Ako se ne poklapa (npr. djelomična kopija iz starog synca), preimenuje se u `~p_<verzija>` i briše u sljedećem prolazu. Ako je u upotrebi, to je greška i pokušava se ponovno.
   Verzije kojih nema na shareu ne dira `VersionSync`, one su posao `DirectoryClean` (`PropagateDeletes`). Sve što se ne može pročitati ostaje kako jest. Popis manifesta čita se jednom (`Directory.GetFiles`), a greška pri čitanju prekida sync tog foldera bez ikakvih izmjena.
   Ako lokalno ostaje novija kompletna verzija, ništa se ne gradi.
   Nepotpuna ciljna verzija se prvo makne u stranu (`~o_`). Ako je u upotrebi, odustaje se prije ponovne izgradnje. Kad izgradnja ne uspije, vraća se na mjesto.
6. Ako na shareu nema `_DELTA\<dir>\manifests`, `EXEDIR` ide starim putem (file sync svih verzija), osim kad je `DeltaSyncRequired=True`. Tada je to greška i nema fallbacka.

```
\\bepo\ARGOSY\_DELTA\EXEDIR\
  manifests\Argosy_2026_09_18__11_41_11.json
  deltas\Argosy_2026_09_07__07_32_04__TO__Argosy_2026_09_18__11_41_11\
    delta.json
    data\12.patch.gz, 57.gz, ...
```

## Konfiguracija

`AppSettings.json` (klijent, po `FolderPair`):

```json
"IgnorePaths": [ "APP", "DfsrPrivate", "_DELTA" ],
"VersionedDirs": [ "EXEDIR", "EXEDIR_X86" ],
"VersionPrefix": "Argosy",
"DeltaDir": "_DELTA",
"DeltaSyncRequired": "False"
```

`ArgosyDeltaBuilder.json` (server): `ShareRoot` (preporuka je lokalni path na bepo, ne UNC), `VersionedDirs`, `ReadyMarker` (vrijedi samo u korijenu foldera verzije), `RequireReadyMarker` (default `false`), `QuietMinutes`, `RetentionDays`, `MinPatchFileSize`, `MaxPatchRatio`, `ExcludeFiles` (`Thumbs.db`, `desktop.ini`, ignoriraju se na **bilo kojoj dubini**, ne smiju biti imena stvarnih fileova aplikacije).

## Redoslijed uvođenja

`_DELTA` je unutar sync roota. Svaki klijent čiji `AppSettings.json` nema `_DELTA` u `IgnorePaths` povući će **sve delte** (stotine MB). Zato:

1. GPO: novi ArgosyUpdater (uključujući `Octodiff.exe` i novi `AppSettings.json`) na sve PC-e.
2. Pričekati dok `dbo.ArgosyUpdaterMachines.ArgosyUpdaterVersion` ne pokaže novu verziju na svim strojevima.
3. Na bepo (Windows PowerShell 5.1, kao admin): `ArgosyDeltaBuilder.exe --whatif`, zatim `.\Install-ArgosyDeltaBuilderTask.ps1 -WhatIf`, zatim bez `-WhatIf`.
4. Kad builder napravi manifeste, drugi GPO s `"DeltaSyncRequired": "True"` u `AppSettings.json`. Bez toga klijent koji na trenutak ne vidi `_DELTA\EXEDIR\manifests` (ACL, mreža) tiho prelazi na stari sync i povuče **sve** verzije. S `True` to je samo greška u logu i `EXEDIR` se taj put ne dira. Ne uključuj prije koraka 3, jer tada PC-i ne bi dobivali nove verzije.
5. Opcionalno: publish skripta na kraju uploada upisuje `_READY` u folder verzije.

Rollback: `Unregister-ScheduledTask -TaskName ArgosyDeltaBuilder -Confirm:$false`, pa obrisati `\\bepo\ARGOSY\_DELTA`. Klijenti se vraćaju na stari sync.

## Poznata ograničenja

- Ako korisnik pokrene *nekompletnu* verziju iz starog synca, ne može se zamijeniti dok je otvorena. Na svaki timer tick javlja se greška, ali se verzija ne gradi uzalud.
- `QuickCheck` lokalne verzije gleda direktorije i veličine fileova, ne hash. Ponovni upload verzije s promjenom iste veličine ne stiže na PC koji tu verziju već ima.
- Putanje duže od `MAX_PATH` (260) padaju u copy/patch. Brisanje leftovera koristi `\\?\`. `_CONFIGS\_ARHIVA\...` je dubok, pa `LocalPath` treba biti kratak.

## Reference

- Octodiff: https://github.com/OctopusDeploy/Octodiff
- NuGet `Octopus.Octodiff`: https://www.nuget.org/packages/Octopus.Octodiff
- .NET long path support: https://learn.microsoft.com/dotnet/standard/io/file-path-formats
- `New-ScheduledTaskTrigger`: https://learn.microsoft.com/powershell/module/scheduledtasks/new-scheduledtasktrigger
