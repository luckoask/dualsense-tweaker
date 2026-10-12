# DualSense Tweaker · od LCKS

Aplikácia pre Windows na nastavenie ovládačov PS5 **DualSense** a **DualSense Edge** cez USB aj Bluetooth. Umožňuje upraviť svetelný pás, indikátory LED, klasické vibrácie a adaptívne spúšte. Podporuje profily, automatické prepínanie podľa spustenej hry, reakciu na zvuk systému aj HD haptiku zo zvuku.

## Inštalácia a spustenie

Verzia **1.1.0** pridáva voliteľnú samostatnú aplikáciu **Macro Gamer** v rovnakom MSI. Predvolene sa neinštaluje. Má vlastný editor makier, Lua skripty, profily a diagnostický/klávesnicový výstup; pôvodné rozhranie Tweakeru ostáva samostatné. [Príručka Macro Gamer](docs/macro-gamer/USER-GUIDE.md) obsahuje inštaláciu, prvé makro a riešenie problémov; [kompatibilita](docs/macro-gamer/COMPATIBILITY.md) uvádza podporované výstupy a rozsah testovania.

**Inštalátor:** `DualSenseTweaker-<verzia>-x64.msi` pre 64-bitový Windows 11. Sprievodca umožňuje vybrať priečinok a pridať odkaz na plochu. Predvolený priečinok je `C:\Program Files\LCKS\DualSense Tweaker`. Inštalátor obsahuje aj prostredie .NET, pridáva odkaz do ponuky Štart a umožňuje odinštalovanie cez *Nastavenia → Aplikácie*. Novšia verzia nahradí staršiu; spustenú aplikáciu najprv korektne ukončí. Pri odinštalovaní sa zruší automatické spúšťanie s Windows, ale profily a nastavenia v `%APPDATA%\DualSenseTweaker` zostanú zachované.

Tichá inštalácia verzie 1.0.7:

```powershell
msiexec /i DualSenseTweaker-1.0.7-x64.msi /qn
```

**Prenosná verzia:** spusti `DualSenseTweaker.exe`. Obsahuje všetko potrebné vrátane .NET. Potom zapni ovládač tlačidlom PS alebo ho pripoj káblom USB-C; aplikácia ho nájde automaticky.

Inštalátor a spustiteľný súbor zatiaľ nemajú digitálny podpis. Pri prvom spustení môže Windows SmartScreen zobraziť upozornenie.

- Zatvorenie okna predvolene skryje aplikáciu do systémovej lišty. Toto správanie môžeš zmeniť v *Nastavenia → Správanie aplikácie*.
- Ľavým kliknutím na ikonu v lište otvoríš okno. Pravým kliknutím zobrazíš profily, automatické spúšťanie s Windows a príkaz **Ukončiť**.
- Voľba **Spúšťať s Windows** spustí aplikáciu po prihlásení na pozadí s parametrom `--tray`.
- Opätovné spustenie otvorí okno už bežiacej aplikácie.
- **Päť rýchlych stlačení PS** otvorí okno aj počas hry. Medzi stlačeniami môže byť najviac 0,7 sekundy. Funkciu môžeš vypnúť v nastaveniach aplikácie. Steam môže na tlačidlo PS reagovať tiež.

## Sekcie aplikácie

| Sekcia | Funkcie |
|---|---|
| **Zariadenie** | Pripojené ovládače, ich názvy a profily, prepojenie nastavení, obnovenie pôvodného stavu, batéria, firmvér, zvuk ovládača a obnovovanie nastavení. |
| **Profily** | Súťažný, Dobrodružný a vlastné profily, načítanie a uloženie do súboru, globálne klávesové skratky a prepínanie podľa hry. |
| **Spúšte** | Samostatné nastavenie L2 a R2: Hra, Vypnuté, Odpor, Cvaknutie a Vibrácia. Náhľad zón 0–9 a tlačidlo Otestovať. |
| **Svetlá** | Stála farba alebo Dúha, jas, výber farby, hodnoty HEX a RGB, živý náhľad, indikátory hráča a mikrofónu. |
| **Dotykový panel** | Ovládanie kurzora, klikanie, ťahanie a posúvanie dvoma prstami. Skratka PS + klik na panel zapne alebo vypne ovládanie myši. |
| **Gyroskop** | Živý 3D náhľad vstupov, grafy snímačov, test mierenia, ovládanie myši pohybom a kalibrácia. Skratka PS + R1 zapne alebo vypne ovládanie myši pohybom. |
| **Vibrácie a haptika** | Ručné klasické vibrácie, útlm vibrácií a spúští, HD haptika zo zvuku cez USB. |
| **Reakcia na zvuk** | Spektrum, basy, stredy a výšky, detekcia úderov basov, zosilnenie, citlivosť, prah, nábeh a doznievanie. Zvuk môže ovládať vibrácie, farbu, jas a záblesky. |
| **Nastavenia** | Slovenčina, angličtina alebo čeština, tmavý a svetlý vzhľad, spúšťanie s Windows, správanie v lište, otvorenie tlačidlom PS, upozornenia, záznamy a informácie o aplikácii. |

### Jazyk a vzhľad aplikácie

V *Nastavenia → Jazyk a vzhľad* vyber **Slovenčina · SK**, **English · EN** alebo **Čeština · CZ** a tému **Tmavý** alebo **Svetlý**. Rozhranie, ponuka v systémovej lište, predvolené názvy profilov a správy aplikácie sa prispôsobia okamžite, bez reštartu. Systémové časti dialógov na výber súborov používajú jazyk Windows.

Voľby sa automaticky ukladajú do spoločných nastavení aplikácie a obnovia sa pri ďalšom spustení. Neovplyvňujú profily ani efekty ovládačov. Pri aktualizácii zo staršej verzie zostane predvolená slovenčina a tmavý vzhľad.

### Svetelný efekt Dúha

V *Svetlá → Efekt* vyber **Dúha**. Predvolený cyklus trvá 8 sekúnd; môžeš ho nastaviť od 1 do 60 sekúnd. Kratší cyklus znamená rýchlejšie prechody. Jas sa nastavuje samostatne a obe strany svetelného pásu zobrazujú rovnakú farbu. Efekt funguje cez USB aj Bluetooth a pokračuje aj s oknom skrytým v lište.

Efekt, dĺžka cyklu a jas sa ukladajú do profilu aj do exportovaného súboru. Jednotlivé farby animácie sa neukladajú. Staršie profily používajú stálu farbu.

Výber svetelného efektu vypne ovládanie svetla zvukom; vibrácie podľa zvuku môžu pokračovať. Spustením reakcie na zvuk, výberom aktívneho svetelného mapovania alebo zapnutím zábleskov pri údere basov opäť odovzdáš svetelný pás zvuku. Voľba **Stála farba** obnoví uloženú ručne vybranú farbu.

### Profily

- **Súťažný:** svetelný pás a indikátory hráča sú zhasnuté, spúšte sú bez odporu, aplikácia nevibruje a vibrácie z hier sú čo najviac utlmené.
- **Dobrodružný:** odpor na oboch spúšťach, silné vibrácie podľa basov a svetelný pás reagujúci na zvuk.
- **Vlastný:** neutrálny východiskový profil. Ďalšie profily vytvoríš z aktuálnych nastavení tlačidlom **Vytvoriť**.

Zmeny sa automaticky ukladajú do aktívneho profilu. Tlačidlo **Obnoviť predvolené hodnoty** vráti profil do východiskového stavu.

Predvolené názvy sa zobrazujú vo vybranom jazyku aplikácie. Uložené identifikátory `Competitive`, `Adventure` a `Custom` zostávajú zachované, takže existujúce profily, skratky a pravidlá pre hry fungujú aj po aktualizácii. Názvy vytvorené používateľom sa nemenia.

### Viac ovládačov

Všetky pripojené ovládače môžu fungovať súčasne, každý s vlastným profilom. Aplikácia si priradenie pamätá podľa MAC adresy aj medzi reštartmi a pri zmene USB/Bluetooth pripojenia.

Kartou v *Zariadenie → Ovládače* vyberieš ovládač, ktorý upravuješ. Jeho názov sa zobrazí aj v hlavičke. Ostatné sekcie upravujú jeho profil; tento ovládač sa používa aj na ovládanie myši a HD haptiku.

Ovládače s rovnakým profilom majú spoločné nastavenia. **Prepojiť ostatné s týmto** priradí ostatným ovládačom profil upravovaného ovládača. **Vlastná kópia** vytvorí samostatný profil s rovnakými nastaveniami. Prepnutie profilu kartou, skratkou, ikonou v lište alebo pravidlom pre hru platí aj pre prepojené ovládače.

Päť rýchlych stlačení PS na ktoromkoľvek ovládači otvorí okno a vyberie tento ovládač na úpravu.

**Obnoviť pôvodný stav** nastaví spúšte bez odporu, zastaví vibrácie, zruší útlm, nastaví modrý svetelný pás a zhasne indikátory LED. Aplikácia odstráni priradenie profilu a uloženú kalibráciu gyroskopu pre tento ovládač. Prestane ho ovládať, kým mu znova nepriradíš profil. Samotné profily zostanú zachované. Párovanie odstrániš samostatne v nastaveniach Bluetooth vo Windows; vnútornú kalibráciu páčok a snímačov aplikácia nemení.

### Skratky a automatické prepínanie

Globálna kombinácia klávesov s číslom 1–9 aktivuje profil na príslušnom mieste v zozname. Funguje aj počas hry a s oknom skrytým v lište. Na rozloženiach klávesnice používajúcich AltGr aplikácia predvolene zvolí **Ctrl + Alt + Shift**, aby skratky neblokovali písanie znakov. Kombináciu môžeš zmeniť v *Profily → Skratky*.

Pre automatické prepínanie pridaj pravidlo `hra.exe → profil`. Aplikácia kontroluje bežiace procesy každé dve sekundy. Po spustení hry aktivuje jej profil a po skončení obnoví pôvodný. Ručné prepnutie počas hry rešpektuje a po jej skončení profil už nemení. Pri viacerých hrách má prednosť pravidlo vyššie v zozname.

### Ukončenie a upozornenia

Pri ukončení, odhlásení alebo vypnutí Windows sa zastavia vibrácie a HD haptika, vypnú sa efekty oboch spúští a útlm sa vráti na nulu. Svetelný pás si ponechá poslednú farbu.

Aplikácia môže upozorniť na prepnutie profilu a slabú batériu pri 15 % a 5 %. Ovládač hlási stav batérie v krokoch po 10 percentuálnych bodoch (5 %, 15 %, 25 % atď.).

## Pripojenie a obmedzenia

| Funkcia | USB | Bluetooth |
|---|---|---|
| Svetelný pás, LED, klasické vibrácie, spúšte a batéria | Áno | Áno |
| Zvuk do reproduktora alebo slúchadiel a nastavenie hlasitosti | Áno | Nie; Windows tu ovládač nevidí ako zvukové zariadenie |
| HD haptika zo zvuku | Áno; vyžaduje štvorkanálový zvukový výstup | Nie; dostupné sú klasické vibrácie podľa basov |

Pre HD haptiku nastav zvukové zariadenie **Wireless Controller** vo Windows na štyri kanály (**Quadraphonic**). Kanály 3 a 4 slúžia haptike. Počas jej prehrávania aplikácia pozastaví vlastné klasické vibrácie, aby haptiku neprerušovali.

### Súbeh s hrami a inými aplikáciami

- **Steam Input** môže posielať vlastné farby, indikátory a vibrácie. Pre konkrétnu hru ho môžeš vypnúť alebo použiť *Zariadenie → Obnovovanie nastavení*; predvolený interval je jedna sekunda.
- V hre s **natívnou podporou DualSense** nechaj spúšte v režime **Hra** a vypni ich obnovovanie, aby aplikácia neprepisovala herné efekty. Ak hra používa vlastnú HD haptiku, vypni haptiku zo zvuku v aplikácii. Tieto nastavenia môžeš uložiť do profilu pre danú hru.
- Cez Bluetooth aplikácia zapína rozšírený režim ovládača. Niektoré hry čítajúce základný vstup cez DirectInput potom nemusia dostávať vstupy, kým ovládač znova nepripojíš.
- Ak ovládač drží iná aplikácia v exkluzívnom režime alebo ho skrýva **HidHide**, DualSense Tweaker ho nemusí nájsť.
- Útlm vibrácií v rozsahu 0–7 vibrácie z hier zoslabuje, ale úplne ich nevypína. Odošlanie iných nastavení môže klasické vibrácie hry krátko prerušiť, kým hra neodošle ďalšiu správu.

### Dotykový panel a gyroskop

Ak Steam mapuje dotykový panel na myš súčasne s aplikáciou, kurzor sa môže hýbať dvojnásobne. Jedno z mapovaní vypni. V hre používajúcej panel na vlastné funkcie môžeš ovládanie myši vypnúť skratkou **PS + klik na panel**.

Windows nedovolí aplikácii ovládať myš v oknách spustených ako správca, ak sama nemá rovnaké oprávnenia. Niektoré hry s ochranou proti podvádzaniu môžu vložené pohyby myši ignorovať.

Gyroskop ovláda myš. Hra musí podporovať súčasný vstup myši a ovládača; inak môže prepínať ikony alebo spôsob ovládania. Nastavenia citlivosti a aktivácie sa ukladajú do profilu, kalibrácia ku konkrétnemu ovládaču. Funkcia Windows **Zlepšiť presnosť ukazovateľa** môže na pracovnej ploche pridávať vlastné zrýchlenie.

Smer otočenia sa bez magnetometra môže postupne odchýliť. V 3D náhľade ho obnovíš tlačidlom **Vycentrovať** alebo stlačením **R3**; mierenie to neovplyvňuje.

### 3D model a licencia

Model **PS5 Controller** vytvoril [Taohid Animation](https://sketchfab.com/3d-models/ps5-controller-b7bb9c5102a04cb0b1966c6d02bad7d6) pod licenciou [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/). GLB adaptácia pochádza z [DualSense Studio](https://github.com/SafaElmali/dualsense-studio/tree/77ad9437c85b0c8990b6a66b167a5f56332757a2). Má 104 240 trojuholníkov a je zabudovaná do aplikácie, takže funguje aj bez internetu. Model je prispôsobený orientácii snímačov a šírke 160 mm; ide o vizuálnu rekonštrukciu, nie rozmerovo certifikovaný CAD. Podrobnosti a vykonané úpravy sú v `src/DualSenseTweaker/Assets/Controller/ATTRIBUTION.txt`.

Náhľad zachytáva aj krátke stlačenia medzi obnoveniami obrazovky a aktualizuje sa približne 60-krát za sekundu. Po odpojení sa model vráti do neutrálnej polohy. Náhľad nemení vstupy hier. Zobrazuje štandardný DualSense; zadné páčky a tlačidlá Fn verzie Edge nemajú samostatnú geometriu.

## Riešenie problémov

- **Ovládač sa nenašiel:** skontroluj pripojenie vo Windows a nastavenia HidHide. Použi **Hľadať ovládač**.
- **Ovládač bliká a nepripája sa:** stlač PS alebo pripoj kábel USB-C. Ak treba obnoviť párovanie, podrž PS + Create, kým nezačne rýchlo blikať, a pridaj ho v nastaveniach Bluetooth vo Windows.
- **Svetelný pás má inú farbu:** ručný výber farby v sekcii Svetlá vypne ovládanie svetla zvukom. Ak chceš svetlo podľa zvuku obnoviť, znova spusti reakciu na zvuk a vyber napríklad **Farba zo spektra**.
- **Svetelný pás po pripojení cez Bluetooth:** aplikácia počká na dokončenie úvodnej animácie ovládača (približne 3,4 sekundy), prevezme ovládanie LED a odošle aktuálnu farbu. Živý náhľad ukazuje požadovanú farbu; ovládač neposkytuje spätné potvrdenie skutočného svietenia.
- **Hra prepisuje nastavenia:** skontroluj Steam Input a obnovovanie nastavení.
- **Záznamy chýb:** `%APPDATA%\DualSenseTweaker\dualsense.log`. Priečinok otvoríš zo sekcie Nastavenia. Pri prekročení 1 MB sa starší záznam presunie do `dualsense.1.log`.

## Uložené dáta

V priečinku `%APPDATA%\DualSenseTweaker` sú profily `profiles\*.json`, spoločné nastavenia `settings.json` a záznamy aplikácie. Profily sa načítavajú a exportujú v rovnakom formáte JSON.

## Zostavenie zo zdrojov

Potrebný je .NET 10 SDK. Použi existujúce zostavovacie skripty:

```powershell
dotnet test
dotnet publish src/DualSenseTweaker -p:PublishProfile=SingleExe
powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1
```

Prenosný súbor a inštalátor vzniknú v priečinku `publish`. Inštalátor používa WiX Toolset v5 s licenciou MS-RL; potrebné balíky sa obnovujú cez NuGet. Ikonu a grafiku inštalátora môžeš podľa potreby vygenerovať skriptmi `tools\make-icon.ps1` a `tools\make-installer-art.ps1`.

### Git a vývoj na ďalšom počítači

Repozitár je na [GitHube](https://github.com/luckoask/dualsense-tweaker). Na ďalšom počítači s Windows potrebuješ Git a .NET 10 SDK. Pri klonovaní súkromného repozitára sa prihlás do účtu GitHub, ktorý k nemu má prístup.

```powershell
git clone https://github.com/luckoask/dualsense-tweaker.git
cd dualsense-tweaker
dotnet restore
dotnet run --project src/DualSenseTweaker -c Release
```

Vetva `main` obsahuje aktuálny zdrojový kód. Značka `v1.0.7` označuje verziu 1.0.7 s efektom Dúha, jazykmi SK/EN/CZ, oboma témami a opravou živého prekladu režimov spúští. Ak chceš klonovať túto konkrétnu verziu, použi `git clone --branch v1.0.7 https://github.com/luckoask/dualsense-tweaker.git`; pre ďalší vývoj potom vytvor pracovnú vetvu.

`.gitignore` vynecháva zostavené súbory, inštalátory, záznamy, zálohy, nastavenia vývojového prostredia a súbory `.env`. Zdrojový 3D model, jeho licencia a grafika inštalátora patria do repozitára. `.gitattributes` určuje textové súbory, ukončenia riadkov a binárne podklady.

Stav projektu a rozdiely zobrazíš príkazmi `git status` a `git diff`. Ďalšie zmeny z GitHubu načítaš príkazom `git pull --ff-only`. Profilové dáta aplikácie sú uložené samostatne v `%APPDATA%\DualSenseTweaker`; pri prenose vlastných profilov a nastavení ich skopíruj do rovnakého priečinka na druhom počítači so zatvorenou aplikáciou.

## Architektúra

| Vrstva | Priečinok | Úloha |
|---|---|---|
| Stav | `Core/State` | Nastavenia profilov, živé výstupy, obnovovanie a predvoľby aplikácie. |
| Zariadenie | `Core/Device` | Vyhľadávanie ovládačov, HID komunikácia, vstupné a výstupné správy, svetelné efekty, adaptívne spúšte a batéria. |
| Pohyb | `Core/Motion` | Spracovanie snímačov, orientácia, kalibrácia, ovládanie myši a test mierenia. |
| Vstup | `Core/Input` | Gestá dotykového panela a ovládanie myši. |
| Zvuk | `Core/Audio` | Zachytávanie zvuku cez WASAPI, analýza spektra, reakcia na zvuk a výstup HD haptiky. |
| Profily | `Core/Profiles` | Ukladanie profilov, priradenie ovládačom, automatické ukladanie a prepínanie podľa hry. |
| Rozhranie | `src/DualSenseTweaker` | WPF a MVVM, témy LCKS, systémová lišta, skratky, automatické spúšťanie a jedna inštancia aplikácie. |

Priečinky `Core/...` zodpovedajú projektu `src/DualSenseTweaker.Core`. Protokol vychádza z verejných implementácií Linux `hid-playstation.c`, `dualsensectl`, Nielk1 TriggerEffectGenerator a SDL `SDL_hidapi_ps5.c`. Formát adaptívnych spúští pochádza z verejného spätného skúmania protokolu, nie z oficiálnej dokumentácie Sony.

Autor: **LCKS**
