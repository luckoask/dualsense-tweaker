# Macro Gamer — používateľská príručka

Macro Gamer je samostatná aplikácia pre Windows 11 x64. DualSense Tweaker vlastní HID pripojenie; Macro Gamer dostáva jeho vstupy cez lokálny kanál. Obe aplikácie musia byť spustené tým istým používateľom s rovnakou úrovňou oprávnení. Macro Gamer sa nespúšťa automaticky a engine je po každom otvorení vypnutý.

## Inštalácia a odstránenie

V jednom MSI ponechaj vybraný povinný **DualSense Tweaker**. **Macro Gamer** je predvolene nevybraný. Jeho vybraním sa nainštaluje samostatná aplikácia, Lua worker, vlastné závislosti a odkaz v Štarte; odkaz na ploche je ďalšia voliteľná položka. Nevybraná súčasť nezanechá súbory Macro Gamer, jeho odkazy ani ovládače zariadení.

Tichá inštalácia samotného Tweakeru:

```powershell
msiexec /i DualSenseTweaker-1.1.0-x64.msi /qn /norestart
# Explicitne iba povinná aplikácia, bez ikony na ploche:
msiexec /i DualSenseTweaker-1.1.0-x64.msi /qn ADDLOCAL=Application
```

Tweaker a Macro Gamer, vrátane oboch ikon na ploche:

```powershell
msiexec /i DualSenseTweaker-1.1.0-x64.msi /qn ADDLOCAL=Application,Desktop,MacroGamer,MacroDesktop
```

Pridanie Macro Gamer do existujúcej inštalácie:

```powershell
msiexec /i DualSenseTweaker-1.1.0-x64.msi /qn ADDLOCAL=Application,MacroGamer
# Oprava súborov už vybraných súčastí:
msiexec /fa DualSenseTweaker-1.1.0-x64.msi /qn
# Odstránenie iba voliteľnej aplikácie:
msiexec /i DualSenseTweaker-1.1.0-x64.msi /qn REMOVE=MacroDesktop,MacroGamer
# Celý produkt:
msiexec /x DualSenseTweaker-1.1.0-x64.msi /qn
```

Na interaktívnu zmenu súčastí použi MSI sprievodcu. Povinný Tweaker sa nedá samostatne odobrať. Odinštalovanie aj aktualizácia zachovajú používateľské dáta. Dáta Macro Gamer sú v `%APPDATA%\DualSenseTweaker\MacroGamer`; dáta pôvodnej aplikácie sa nemigrujú. Pred odstránením inštalátor požiada spustené aplikácie o ukončenie. Pre podrobný inštalačný log pridaj `/l*v cesta-k-logu.txt`.

## Prvé funkčné makro

1. Spusti **Macro Gamer** z ponuky Štart alebo `C:\Program Files\LCKS\DualSense Tweaker\MacroGamer\MacroGamer.exe`.
2. V **Ovládače** klikni **Otvoriť DualSense Tweaker**. Pripoj DualSense alebo Edge cez USB/Bluetooth. Živé údaje musia zodpovedať skutočnému ovládaču; pri chýbajúcom zdroji sa makrá pre klávesnicu nedajú zapnúť.
3. Najprv nechaj **Diagnostický výstup** v **Profily**. V **Makrá** povoľ predvolený príklad **R1 → E tap**, ulož profil a klikni **Zapnúť engine**.
4. Stlač a pusť R1. V **Diagnostika** sa objavia skutočné udalosti Down/Up diagnostického backendu. Tým overíš celý vstupný tok bez odosielania klávesov do hry. Diagnostické makro možno tlačidlom **Spustiť makro** overiť aj bez ovládača.
5. Pre výstup do aplikácie vyber v profile **Klávesnica a myš**, ulož a znovu zapni engine. Otvor napríklad Poznámkový blok, daj mu fokus a stlač R1; podporovaná aplikácia dostane kláves E. Macro Gamer automaticky nepresúva fokus na hru.
6. Použi **STOP VŠETKO** alebo globálne **Ctrl+Shift+F10**. Engine sa vypne a uvoľní svoje držané vstupy. Pri obsadenej skratke aplikácia oznámi konflikt a nepovolí zapnutie enginu. Zatvor aplikáciu, ktorá skratku používa, a znovu otvor Macro Gamer.

Povolenie makra v profile a zapnutie enginu sú dve samostatné voľby. Importované makrá a skripty sú deaktivované. Prepnutie profilu, ovládača alebo backendu vyžaduje nové explicitné zapnutie. Strata spojenia, medzera vo vstupoch, chyba výstupu a chyba/časový limit skriptu zastavia aktívne výstupy.

## Editor makier a nahrávanie

**Editor makier** obsahuje poradie akcií, pridanie/odstránenie, presun šípkami a parametre vybranej akcie. Akcie: čakanie; stlačenie, uvoľnenie alebo tap klávesu/myši; relatívny pohyb myši; koliesko. Časy sú v milisekundách. Podporované klávesy: A–Z, 0–9, F1–F24, medzera, Enter, Tab, Escape, Backspace, Insert/Delete, Home/End, PageUp/PageDown, šípky, CapsLock a ľavé/pravé Ctrl/Shift/Alt. `CTRL`, `SHIFT`, `ALT` označujú ľavý modifikátor. Tlačidlá myši: LEFT, RIGHT, MIDDLE, X1, X2.

Spúšťače: manuálne spustenie; press/release; držanie; kombinácia (`L1+R1`); prekročenie prahu L2/R2; pripojenie pri zapnutom engine. Dodatočné vyžadované tlačidlá tvoria podmienku. Prvý vzorok po pripojení nevytvára falošnú udalosť stlačenia. Odpojenie je vždy zastavenie. Režim Once prehrá zadaný počet opakovaní, Hold končí uvoľnením podmienky, Toggle prepína beh ďalším fyzickým spustením. Opakovanie počas aktivity má stále maximálny čas.

**Nahrať vstupy** vyžaduje skutočný vybraný ovládač a vypne výstup. **Zastaviť záznam** uloží záznam do profilu. Uprav tabuľku tlačidlo → kláves a zvoľ **Vytvoriť makro zo záznamu**. Nové makro je deaktivované, má manuálny spúšťač a prehráva mapované digitálne prechody. Povoľ ho, ulož profil, zapni engine a použi **Spustiť makro**. Analógové a dotykové dáta sú v zázname; tieto údaje sa neprehrávajú ako virtuálny gamepad. Mapovania sa ukladajú s profilom.

Záznam má `formatVersion: 1`, `controllerId` a pole `points` s `offsetUs` a celým normalizovaným `frame`. Čas je monotónny v mikrosekundách. Ukladajú sa digitálne prechody, analógové zmeny aspoň 3/255 najskôr po 16 ms a dotykové zmeny aspoň 8 pixelov. Záznam má najviac 60 sekúnd/8192 bodov a nesmie spájať rôzne fyzické generácie pripojenia. Profil podporuje 16 záznamov. Nezmenené vstupné rámce sa neskladujú bez obmedzenia.

## Skripty a profily

Príklady makier `r1-tap.json`, `combo-tap.json` a `trigger-hold.json` nájdeš v nainštalovanom priečinku `MacroGamer\samples`; načítaj ich cez import makra. Sú predvolene vypnuté. Uloženie profilu počas nahrávania záznam ukončí a zachová zachytené body. Automatický výber hry sa počas nahrávania odloží. Limit súboru profilu 4 MiB platí aj pre súčet uložených záznamov.

V **Skripty** vytvor alebo načítaj `.lua`, použi **Overiť**, povoľ ho v profile, ulož, zapni engine a použi **Spustiť skript**. Overenie spúšťa skutočný izolovaný interpreter bez výstupu na plochu. Editor ukazuje chyby vrátane riadkov, ktoré Lua poskytne. Knižnica obsahuje päť overovaných príkladov; úplné API je v [SCRIPTING.md](SCRIPTING.md). Úprava a uloženie textu neprepíšu snapshot bežiaceho skriptu. Na spustenie novej verzie najprv zastav skripty a znovu zapni engine.

**Profily** umožňujú vytvorenie, premenovanie uložením názvu, duplikovanie, odstránenie a JSON import/export. V **Ovládače** priraď fyzickú identitu alebo automatický prvý ovládač. Herné priradenie môže byť názov `.exe` alebo cesta. Automatický výber sleduje program v popredí; prepnutie profilu engine vždy vypne. Číselné premenné sú JSON objekt, napríklad `{"tap_ms":60}`. Nastavenia jazyka SK/EN/CZ a témy sa ukladajú oddelene od Tweakeru.

Pri zatvorení sa platné úpravy uložia. Neplatné rozpracované číselné polia sa uchovajú v `editor-draft.json` a po otvorení obnovia s vypnutým enginom. JSON import má limit 4 MiB; neznáme polia, nesprávna verzia a neplatné hodnoty sa odmietnu. Poškodené profily sa zachovajú na disku a chyba sa zapíše do diagnostiky.

## Riešenie problémov

| Stav | Postup |
| --- | --- |
| Zdroj vstupov offline | Otvor Tweaker tlačidlom v Ovládače. Obe aplikácie spusti rovnakým používateľom bez rozdielnej elevácie. Over pripojenie v pôvodnej aplikácii. |
| Vstupy sa zobrazujú, hra nereaguje | Over diagnostický backend, potom výstup v Poznámkovom bloku. Skontroluj fokus, mapovanie hry, Steam Input a oprávnenia procesu. Prijatie SendInput závisí od konkrétnej aplikácie. |
| Skript prekročil limit | Zmenši slučku/počet akcií alebo rozdeľ logiku medzi fyzické udalosti. Timeout worker ukončí a engine zastaví. |
| Virtuálny ovládač je nedostupný | Žiadny overený distribuovateľný virtuálny ovládač sa neinštaluje. Použi diagnostiku alebo klávesnicu/myš. |
| Chýba Lua worker | Oprav vybranú MSI súčasť MacroGamer. Nekopíruj iba MacroGamer.exe bez jeho priečinka. |
| Držaný kláves sa neuvoľnil | Znovu použi STOP VŠETKO a skontroluj chybu SendInput. Aplikácia zadrží chybnú položku v registri držaných vstupov a umožní zopakovať uvoľnenie. |

Diagnostiku exportuj cez **Exportovať záznam**. Odpoj/reštartuj ovládač a po obnovení stabilného spojenia znovu zapni engine. Windows nútené ukončenie procesu alebo výpadok napájania nemôžu vykonať jeho kód na uvoľnenie vstupov; pre takéto ukončenie nie je dodaný externý watchdog. Bežné zatvorenie a zastavenie používajú riadené uvoľnenie.

Kompatibilita a presný rozsah overenia sú v [COMPATIBILITY.md](COMPATIBILITY.md) a [ENGINEERING-REPORT.md](ENGINEERING-REPORT.md).
