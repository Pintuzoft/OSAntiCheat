# OSAntiCheat — Roadmap / Funktionslista

Server-side anticheat för CS2 byggt som **CounterStrikeSharp**-plugin i C#.

> **Grundprincip:** Vi ser bara vad servern ser (positioner, view angles, skott,
> träffar, timing, input). Vi har INTE tillgång till klientens minne, processer
> eller filer. Allt nedan är heuristik/statistik → vi flaggar sannolikheter, inte
> bevis. Standardrespons i v1: **logga + admin-notis** (ingen auto-ban).

---

## ✅ Går att bygga server-side (prioriterat)

### Kärna / infrastruktur
- [ ] Plugin-skelett (CounterStrikeSharp `BasePlugin`, load/unload, config)
- [ ] Per-spelare tick-buffer (ringbuffer med senaste N ticks: pos, angles, flags)
- [ ] Event-hooks: `WeaponFire`, `PlayerHurt`, `PlayerDeath`, `RoundStart/End`
- [ ] `OnTick`/timer-sampling av view angles & position
- [ ] Detektor-interface (`IDetector`) + registrering
- [ ] **"Suspicion score" per spelare — sensor-fusion / triangulering.** Ingen enskild
      signal dömer; korroboration över *oberoende* axlar (aim-snap, LOS-korrelation, timing,
      rotation) ger säker bedömning. Design:
  - Varje detektor ger normaliserad **confidence 0–1** (inte på/av) → 40° snap > 12° snap.
  - **Viktad summa** → score per spelare, som **avklingar över tid**.
  - **Korroborations-bonus**: flera *olika* detektorer i samma tidsfönster/duell väger mer
    än samma totalpoäng från en detektor (sammanhängande historia > lösryckta tal).
  - **Temporal klustring**: signaler runt samma duell > utspridda.
  - **Tiers**: `watch` (logga tyst) → `review` (admin-notis). Aldrig auto-action på 1 signal.
  - ⚠️ Vikta efter *genuin* oberoendhet — duktiga spelare är bra på allt samtidigt, så
    signalerna korrelerar. Triangulering gör oss säkrare, inte säkra. Människa i loopen.
- [ ] Loggning till fil (JSON-lines) + strukturerad händelsemodell
- [ ] Admin-notis (chatt/console till admins, ev. Discord-webhook)
- [ ] Config (trösklar, på/av per detektor, whitelist av SteamID)

### Detektorer (v1)
- [ ] **Aimbot — angle snaps**: pitch/yaw-delta precis före `WeaponFire`; flagga
      onaturligt stora snaps. OBS: referenspunkt = **närmaste hurtbox-punkt**, INTE
      huvudet (cheats kan låsa till chest/pelvis/närmaste bone). HS% duger ej som signal.
  - ⚠️ **Lagg-robusthet:** nätverkslagg (choke/loss/interpolation) ger diskontinuerliga
    vinkelhopp som *ser ut* som snaps. Skillnad: lagg-hopp är **fiende-agnostiskt** (landar
    var som helst), aimbot-snap **terminerar på hurtbox** + upprepas. Flagga aldrig på
    hoppets storlek ensam. Upptäck lagg via glapp i `Sequence` / stort `Time`-delta → nedvikta.
  - ✅ **Upprepnings-gating (byggt):** en enda snap = legitim flick → tyst. Varje kvalificerad
    snap loggas i ett rullande per-spelare-fönster (`WindowSeconds`); confidence = 0 under
    `MinSnaps`, rampas upp med antalet. Detektorn "lever" men talar först när mönstret upprepas.
- [ ] **Triggerbot**: mät tid mellan "crosshair-på-fiende" och skott; extremt låg
      varians / konsekvent sub-mänsklig reaktionstid = misstänkt
- [ ] **Spinbot**: yaw-rotationshastighet över ticks som överskrider mänskligt möjligt

> **Designregel för all aim-analys:** referensmålet är alltid *närmaste punkt på
> fiendens hurtbox-set* (närmaste bone/hitbox till aim-vektorn), aldrig en fast
> kroppsdel. Cheats kan sikta valfri kroppsdel + slumpa offset inom hitboxen.

### ⭐ Flaggskepp: `AimCorrelation` — gemensam primitiv (hög prioritet efter v1)
En enda mätning per tick: vinkeln mellan spelarens **view-vektor** och riktningen till
**närmaste fiende-hurtbox**, plus **LOS-flagga** (fri sikt eller blockerad av geometri).
Wallhack, soft aim och "tittar in i väggar" är alla uttryck för *samma* storhet — bara
olika LOS-tillstånd och aggregeringssätt. Byggs som ETT subsystem, tre signaler:

- [ ] **Primitiv `AimCorrelation`**: per tick → (vinkelfel till närmaste hurtbox, view-mot-
      fiende-korrelation, LOS ja/nej, "sett fienden senaste T sek" ja/nej).
- [x] **Passiv wallhack (blick/gaze) — BYGGD (v0.3.0):** `wallhack.gaze`. Flaggar när blicken
      *följer* en osedd fiendes rörelse — view-yaw:s hastighet korrelerar med fiendens bäring-
      ändring (inte bara pekar i riktningen → skiljer bort common angles). Round-start-fönstret
      viktas högre (ingen spottad, inga ljud/callouts än). Mjuk kon (~25°) fångar snegling, ej
      bara lås. Score i "sekunder av följning", avklingar. Konfigurerbar. Kompletterar hårda
      `wallhack.track` → triangulering.
  - ⏭️ Vidare: populations-relativ baseline (folkmassan = normalt), "sneglar + ändrar riktning"
      (repositionering som pre-reaktion), aggregerad blick-andel över hela rundan.
- [x] **Aktiv wallhack (tracking/lock) — BYGGD (v0.2.0):** flaggar när aim följer en *rörlig*
      fiende som servern anser **osedd (ej spotted) av observatören**. LOS via CS2:s inbyggda
      spotted-system (`EntitySpottedState.SpottedByMask`) — validerat med `css_osac_los`, en
      klient-wallhack sätter aldrig server-side spotted. Kräver fiende-*rörelse* (ej statisk
      hold) + upprepning via score-motorn. Konfigurerbar (aim-tröskel, min-track, min-move).
  - ✅ **Object permanence gratis:** spotted-flaggan har minne — såg du fienden nyss ligger
      `spotted=True` kvar en stund, så att tracka en nyss-sedd fiende bakom vägg flaggas INTE.
      Exakt det undantag vi designade, kodat av spelet självt. (Verifierat på server: flaggade
      när boten aldrig setts, tystnade när den setts först.)
  - ⚠️ **Kalibrering att mäta:** hur länge ligger spotted kvar? För länge = lucka (se fiende
      1x → tracka "gratis" sen). För kort = korrekt object permanence. Mät på servern; ev.
      komplettera med "tid sedan senast *faktiskt* synlig" om persistensen är för generös.
- [ ] **Aktiv wallhack (tracking/lock)** — vidareutveckling: diskreta events där aim *följer* fiende
      Diskriminator: tracking, inte statiskt sikte → skiljer bort prefire på common angles.
      FP-skydd: kräv **N events inom tidsfönster** (avklingande räknare); ett enstaka = OK
      game sense/ljud/prediktion. Object-permanence-undantag (nyss sedd fiende bakom vägg).
  - ⚠️ **Största FP-källan: ömsesidiga holds + standardspots + slump.** Härdning:
    (a) **poängsätt aldrig statiskt sikte** — bara spårad rörelse (slår ut slump + holds);
    (b) **INLÄRD common-angle-prior, ej handkodad** — standardspots är ogörliga att pinpointa
    manuellt, så låt **populationen definiera normalt**: jämför spelarens frekvens av
    "sikte-mot-verklig-fiende-genom-vägg" på plats L mot *hela serverns* frekvens på L
    (populations-relativ baseline) och/eller bygg auto-heatmap av common angles per karta
    från serverloggar. Folkmassan = baseline, ingen kartkännedom krävs;
    (c) **asymmetri** (bara en spårar den andra som rör sig ovetande) > ömsesidig hold;
    (d) spårning som **lämnar** common-linjen = starkt; (e) **upprepning** (slump upprepas ej).
> **Princip — information exculperar (team-nivå).** `t_info` beräknas för HELA laget:
> fienden räknas som känd så snart spelaren **eller någon levande lagkamrat** fått LOS
> eller hörbart ljud (callouts propagerar vetskap). Ljud-villkoret = "hörbart för mottagaren"
> (servern vet sann utbredning: avstånd + ocklusion). Vi kan INTE höra/tolka röstchatt →
> konservativt antagande att all team-kontakt propagerar fullt ut. Graderad exculpation:
> - **Grov** team-känd position (callout) → sänker confidence mycket, men rentvår INTE
>   *precis kontinuerlig tracking* (callout ger ungefärlig position, ej hurtbox-spårning).
> - **Färskhet**: callout > ~få sek gammal är inaktuell (fienden har flyttat sig).
> - **Timing**: reaktion < ~1s efter lagkamrats första kontakt är för snabbt för en callout.
> Residual efter filtrering (fiende INGEN i laget kunde känt till, ändå precist spårad) =
> sällsynt men starkt. Individuell LOS/ljud = starkaste exculpation; wallbang med info = OK.

- [ ] **Pre-reaction / informations-kausalitet (timing-axel)**: per reaktions-event (aim-snap,
      prefire, plötsligt stopp/reposition) beräkna `latens = t_react − t_info`, där `t_info` =
      tidigaste legitima vetskap (första LOS **eller** första hörbara ljud). Latens < ~100ms
      eller negativ = reagerade på information spelaren inte hade. Upprepat = starkt tell.
  - Oberoende **timing-axel** → utmärkt korroborations-partner till geometri-korrelationen.
  - ⚠️ Kräver **ljudmodellering** för `t_info` (fotsteg/skott/reload; avstånd, ocklusion) —
    annars flaggas legitima ljudreaktioner. Prefire på inlärda common spots måste bort
    (tracking av *verkligt* tillstånd vs generell vana). Svår men högt värde.
- [ ] **Silent first-shot wallbang (diskret event, låg FP)**: flagga skott som (1) penetrerar
      geometri (wallbang, syns i bullet-tracen), (2) mot mål **utan LOS** till skytten, (3) med
      **inget lokaliserande ljud hörbart för skytten** inom `T`, (4) **första-skotts-precision**
      på hurtboxen (inte spray som täcker ytan), (5) bonus: mål på **off-common-spot**. Varje
      villkor höjer confidence. Rent diskret event → lättare bygga + lägre FP än kontinuerlig
      tracking. Delar audibility-modellen med pre-reaction-detektorn.
- [ ] **Shot-timing mot osedd fiende (håll-vinkel-triggern)**: spelaren håller en vinkel
      (legitimt) men **avfyrar exakt när en fiende är vid den punkten** trots att fienden
      **aldrig varit i LOS** och var **tyst** (inget hörbart ljud). T.ex. AWP mot dörr, fienden
      rör sig mot dörren men syns aldrig, skott avfyras. Diskriminator: skott-timing korrelerar
      med fiendens *verkliga* ankomst (ej fiende-agnostisk spam), upprepat. Skottet behöver
      inte penetrera — bara timas mot en osedd, tyst fiendes verkliga position. Delar LOS +
      audibility-modellen.
- [ ] **Soft aim (LOS-grenen)**: samma korrelation men *med* fri sikt = aim assist. Se nedan.
- ⚠️ **Teknisk risk (måste verifieras FÖRST):** hela subsystemet kräver **world-raycast
  server-side** för LOS. Enda delen som behöver världsgeometri. Beror på om CounterStrikeSharp
  exponerar ett trace/ray-API — annars måste vi approximera. → utreds före implementation.

### Detektorer (senare, samma datakälla)
- [ ] **Silent aim**: jämför skottvektor (bullet trace) mot eye-angles vid `WeaponFire`;
      träff mot fiende som inte ligger under crosshairet = starkt signal. Mest görbart.
- [ ] **Soft aim (aggregat, research)**: dela aim-delta i komponent mot/bort från närmaste
      hurtbox-punkt över många dueller; ihållande positiv bias + låg varians + jämn
      exponentiell approach = misstänkt. FP-känsligt → stora sampel, låg vikt, "granska".
- [ ] **Accuracy/HS-anomali**: rullande statistik på headshot-% och träff-% vs baseline
- [ ] **No-recoil / no-spread**: skottspridning för tät jämfört med vapnets recoil-mönster
- [ ] **Bhop-scripts**: perfekt jump-timing på landningstick (varning: false positives)
- [ ] **Fake-lag / choke-mönster**: onormala command/tick-mönster

---

## ⚠️ Begränsat / kräver mer jobb
- [ ] Spelar-baseline & maskininlärning på loggad data (offline-analys, inte live)
- [ ] Replay/demo-markörer så admins kan granska flaggade situationer
- [ ] Cross-server SteamID-reputation (delad databas)

---

## ❌ Går INTE server-side (skrivet ner så vi inte försöker)
- Läsa klientens RAM / hitta injicerade cheats-DLL:er
- Fil-/processignaturer på klienten (det är VAC/kernel-AC:s domän)
- Skärm-/overlay-detektering (ESP, wallhack visuellt) — vi kan bara sluta oss till
  wallhack *indirekt* via beteende (t.ex. pre-aim genom väggar)
- Garanterat 0% false positives

---

## Beslut (v1)
- Ramverk: **CounterStrikeSharp**
- Startdetektorer: **Aimbot (angle snaps), Triggerbot, Spinbot**
- Respons: **logga + admin-notis** (ingen kick/ban i v1)

---

## Validering (2026-07-15) — vad som faktiskt är bevisat

### ✅ Null-test: signalen är verklig, inte en sök-artefakt
Samma svep (1600 configs) med **legitima** spelare utpekade som "fuskare", med speltider
matchade mot de riktiga fuskarnas (0.5–8.8 min):

| Set | Configs som rankar alla tre #1 |
|---|---|
| NULL A (3 legit-kontroller) | **0** / 1600 |
| NULL B (3 legit-kontroller) | **0** / 1600 |
| NULL C (3 legit-kontroller) | **0** / 1600 |
| **Riktiga fuskare** (3 st) | **196** / 1600 |

Sökproceduren kan alltså inte trolla fram "fuskare" ur godtyckliga spelare. 196 vs 0.

### ✅ Negativ kontroll: detektorn mäter inte skicklighet
Bästa legit-spelarna i sina bästa sessioner: 0.10 · 0.07 · 0.00 · 0.00 (identiteter i
private/ban-analysis.md). 133 legit-sessioner, ingen nådde den lägsta fuskaren.

### ❌ MEN: pluginen räknar inte det vi validerade
Vi validerade **signaler per minut**. Pluginen larmar på **peak decayed score**, som är
tidsblind — med glesa signaler blir den i praktiken "största enskilda signalen". Resultat:

  en stammis (legit)      1.04  ← över Watch
  en fuskare (FUSK)       0.75  ← under elva legitima spelare

Separationen försvinner helt. Fuskarna bannas dessutom på 0.8–4.3 min och hinner aldrig
ackumulera — varje tröskel som kräver flera signaler nära varandra missar dem per konstruktion.

- [ ] **Laga score-motorn → Poisson-överraskning**: hur osannolikt är N signaler på T
      alive-minuter givet baseline (0.026/min)? Då vägs exponeringstid in korrekt.
      `fuskare: 1 sig / 0.8 min → p=2%` vs `stammis: 2 sig / 21.7 min → p=11%`.
- [ ] **Kör 100–200 demos** för en riktig baseline + fler null-tester (17 097 finns i arkivet).
- [ ] **Slå INTE på admin-chatt** förrän scoren mäter det vi validerat.
- [ ] `aimbot.snap` / `triggerbot` är **helt ovaliderade** — replay saknar `WeaponFire`-hook.

---

## Storkörning 2026-07-16: 6,1M skott, 717 spelare — allt föll

Körde hela arkivet (17 097 demos, varav 10 515 är CS:GO/Source 1 och inte kan läsas).
**6 172 CS2-demos parsade = 93,8% av det läsbara. 41 723 spelar-sessioner, 6 157 639 skott.**

### ❌ Target-switch (deg/s) — FALSIFIERAD
En känd legit-referens flaggas som **0,32x av "det mänskliga golvet"**. I princip varje
spelare med nog med sampel har en 62–94 ms-outlier. Ett golv alla underskrider är inget golv.

**Rotorsak (min bugg):** i skott-exporten letas "närmaste fiende" **utan vinkelgräns**
(`bestErr = float.MaxValue`). Sprayar man mot ingenting kan "närmaste fiende" vara 120° bort, och
nästa skott 62 ms senare kan en annan fiende 120° åt andra hållet vara närmast → falskt "målbyte"
på 90°+ / 62 ms. Jag påstod att stora vinklar var immuna mot artefakter — **det var fel**.

- [ ] **Fix:** räkna bara målbyte när BÅDA skotten var *på* sina mål (aimErr < ~5°).

### ❌ Dwell (onTargetMs) — DÖD
Med `--min-shots 50` såg listan lovande ut (`wolf_gbg` 94 ms / 27,7% mot referensens 375 ms / 6%).
Men listan sorterade i praktiken på **stickprovsstorlek** — alla i topplistan hade 55–330 skott.
Med `--min-shots 2000`: hela spannet blir **234–375 ms** och referensspelaren hamnar mitt i klungan.
Ingen separation. (`wolf_gbg` är gammal stammis som spelar sällan — 130 skott.)

### ❌ Peak score — mäter exponering
Topp-20 är enbart de mest spelande stammisarna. Fuskarna låg på 0,72–0,75 = under populationens p99.

### ⚠️ Vad detta betyder
**Inga misstänkta hittades — men det betyder inte att servern är ren.** Det betyder att
instrumenten inte fungerar. Vi kan inte säga något alls om 3,5 års speldata.

### Lärdom
Alla tre måtten såg ut att fungera på små stickprov och kollapsade mot riktig data. Domänkunskap
fångade det statistiken missade (*"wolf är ju gammal stammis"*). Sanity-checka alltid topplistor
mot någon som känner spelarna innan de visas för en admin.

---

## CS2CD-resultat (2026-07-16): 335 verifierade fuskare — timing-måtten är definitivt döda

Körde CS2CD-datasetet (317 matcher med VAC-bannade + manuellt verifierade fuskare).
**335 fuskare vs 974 icke-fuskare med 200+ skott på mål.** Riktig statistik, inte fem signaler.

### ❌ Dwell (tid i siktet före skott) — DÖD
```
fuskare:      median 281 ms  |  skott <30ms: 24,9%
icke-fuskare: median 297 ms  |  skott <30ms: 23,3%
```
16 ms isär. En triggerbot ska ha ~20 ms median — de har 281. Måttet duger inte, punkt.

### ❌ Target-switch — DÖD, och inverterad
```
             90-180°:  fastest   p1     p5
fuskare:                  891  2609   6078
icke-fuskare:             188  1609   5984   ← SNABBARE än fuskarna
```
Även med switch-fixen (båda skotten på mål) separerar det inte. De icke-bannade är snabbare.

### ⚠️ Träffprocent / HS% — svag men verklig signal
```
             hit% median  p90   |  HS% median  p90
fuskare:           23,6  33,8   |       28,8  50,6
icke-fuskare:      19,4  26,9   |       17,2  29,4
```
HS% är det starkaste vi mätt (1,7×) — ironiskt, då jag dag 1 avfärdade det som opålitligt
(cheats kan sikta på valfri kroppsdel). Men fördelningarna **överlappar kraftigt**:
icke-fuskarnas p90 (29,4) ligger över fuskarnas median (28,8). Var tionde legit spelare ser
ut som en median-fuskare. Duger för populationsstatistik — **inte för att peka ut en individ**.

- [ ] **Avgörande test:** vad har OSAntiCheats stammisar för HS%? CS2CD:s "legit" är slumpmässiga
      matchmaking-spelare. Ligger topp-legitspelarna på 30%+ är även HS% dött för vår population.

### Sammanfattning av alla mått
| Mått | Testat mot | Utfall |
|---|---|---|
| wallhack.track | 3 admin-bannade | Ovaliderad (5 signaler, 0,8–4,3 min, config tunad på dem) |
| wallhack.gaze | 41 723 sessioner | ❌ Fyrar på 100% |
| aimbot.snap / triggerbot | 6,1M skott | ❌ Noll signaler |
| dwell | **335 verifierade fuskare** | ❌ 281 vs 297 ms |
| target-switch | **335 verifierade fuskare** | ❌ Icke-fuskare snabbare |
| hit% / HS% | **335 verifierade fuskare** | ⚠️ 1,7× men överlappar |

---

## v0.6.0: aimbot.snap ersatt av aimbot.sweep — pluginen detekterade ingenting

Pintuz satte fingret på det: "vi har ju aim och triggers, och om inte ens dem gör det så
kommer den inte detekta nånting?" Korrekt. Pluginen på servern hittade inget för att den
**inte kunde** hitta något — inte för att servern var ren.

| Detektor | Status före v0.6.0 |
|---|---|
| aimbot.snap | 0 signaler / 6,1M skott — krävde 25°/tick = **1600°/s**, fysiskt omöjligt |
| triggerbot | 0 signaler — **otestad**, se retraktionen nedan |
| wallhack.gaze | avstängd, 100% FP |
| wallhack.track | otestad |
| sweep-through | 14× lyft — **fanns bara i offline-verktygen** |

`aimbot.snap` mätte fel axel: **hur snabbt** siktet rör sig. Det finns inget omänskligt värde —
bra spelare flickar hårt hela tiden. Det omänskliga är att **träffa medan man far**.
Borttagen och ersatt av `AimbotSweepDetector` (`aimbot.sweep`).

### CS2CD, tröskel satt över icke-fuskarnas p99
```
sweep-through   >16,1%   fångar 15,4% av 254 fuskare   (1,1% FP)   14× lyft
HS%             >50,0%   fångar 10,2% av 1137          (1,0%)      10×
hit%            >36,2%   fångar  6,9% av 1137          (1,0%)      6,9×
skott <30 ms    >34,6%   fångar  1,8% av 335           (1,0%)      1,8×  ← slumpen
```
Dwell är dött i **båda** inramningarna (median och svans). Det kommer inte tillbaka.

- [ ] **Tröskeln 16,1% är avläst på slumpmässiga MM-spelare.** Stammisar är inte det.
      Kör arkivet och se var topp-legitspelarna landar. Över 16% → måttet mäter skicklighet.
- [ ] **Testa sweep efter target-switch** (Pintuz idé): switch-*hastighet* var dött (fuskare
      var långsammare), men landningen efter ett byte är den skarpaste varianten — en hand
      måste hitta det nya målet först. Mäts nu i measure_cs2cd.py, gratis, kolumnerna fanns.
- [ ] Fusionera sweep + HS% — de mäter olika saker (siktets bana vs var kulan hamnar).
- [ ] `triggerbot` är **otestad**, inte motbevisad. Behåll tyst, räkna inte som skydd.

**Vi missar 85% av fuskarna.** Granskningsflagga, aldrig dom. Auto-ban uteslutet.

---

## Insikten som borde kommit först: nivå vs information

Pintuz: "vi måste ju hitta vad en cheater beter sig som en icke-cheater inte gör"

Titta på vad varje mått vi byggt frågar:
```
dwell, switch, hit%, HS%, sweep, arc  →  "hur BRA är den här spelaren?"
```
Men **en fuskare med aim-assist ÄR statistiskt bara en väldigt bra spelare** — det är exakt vad
cheatet säljs för att göra. Därför överlappar allt, därför landar alla sex på 1,2–1,8×.
**Det är strukturellt, inte otur.** Och det är därför frågan "vad träffar våra bästa spelare?"
hängt över varje resultat: svaret "ungefär samma" hade dödat vart och ett.

Det som är kvalitativt annorlunda är inte att fuskaren siktar bättre. Det är att hen **vet saker
hen inte kan veta.** Ingen mängd skicklighet ger information om en osynlig fiendes position.
Det är ingen skala — det är en informationskanal som inte ska finnas.

### Nolltestet (nytt, i DemoReplay + Analyze)
```
För varje tick där fiende E INTE är spottad av P:
  err_nu   = vinkelfel från P:s sikte till E:s position NU
  err_null = vinkelfel från P:s sikte till E:s position 1,5 s SEDAN
  kvot     = träffar_nu / träffar_null

~1,00×  = siktar på PLATSER, inte personer → map-kunskap, ljud, minne
>>1,00× = siktet följer NUET genom vägg → oförklarligt
```
**Nolltestet absorberar hela skicklighetsconfounden.** Game sense korrelerar ditt sikte lika
mycket med fiendens *gamla* position som hans nya — båda är bara platser på kartan. Att följa
nuet är det enda som inte går att förklara. Kvoten borde läsa likadant för en femårig stammis
som för vem som helst — vilket ingen av de sex nivåmåtten gjorde.

- [ ] **Sanity check först:** är populationens p50 ~1,00×? Är den inte det är mätningen trasig.
- [ ] Sen: har de 3 admin-bannade en högre kvot? (5 signaler → svagt, men enda labels vi har)
- [ ] Håller det: bygg om `wallhack.track` kring kvoten istället för diskreta händelser.

Detta är samma premiss som `wallhack.track` alltid haft — flaggskeppet var rätt hela tiden.
Skillnaden är att kvoten har ett **nolltest inbyggt**, vilket ingen av våra detektorer haft.

---

## RETRAKTION (2026-07-16): "triggerbot-signaturen finns inte" var inte stött av datan

Pintuz frågade: "i cs2cd, står det vad fuskare har? wallhack? aim? trigger?"

Svaret: **nej.** Varje fuskarpost är `{"steamid": "Player_3"}` — inget typfält alls.

Jag skrev in i TODO att CS2CD visar att triggerbot-signaturen inte finns, baserat på att
335 poolade fuskare låg på 6,6% mid-sweep-träff istället för de ~100% en trigger ger. Men
utan typetiketter mätte jag 335 blandade fuskare och drog en slutsats om en delmängd jag
aldrig verifierade fanns. Fel ställd fråga, inte bara ett svagt påstående.

### Vad datan FAKTISKT stödjer
```
skott <30ms:  fuskare 24,9%  icke-fuskare 23,3%
om 10% körde trigger (~90% snabba skott): 0,9x23,3 + 0,1x90 = 30,0%
observerat: 24,9%  →  trigger-användare är högst ~2-3% av gruppen
```
- ✅ Trigger-användare är **sällsynta** i CS2CD (bounden ovan håller)
- ❌ Vår `TriggerbotDetector` är **otestad** — vi kan inte peka ut de få som kanske kör trigger
- ✅ `sweep-through` 15,4% recall står kvar — det var alltid ett poolat påstående

### Vad som fortfarande gäller (poolade påståenden, oberoende av typ)
| Mått | Utfall |
|---|---|
| sweep-through | 15,4% @ 0,5% FP mot ren negativ = **30x lyft** |
| HS% | 10,2% @ 1,0% |
| hit% | 6,9% @ 1,0% |
| dwell | 1,8% @ 1,0% = slumpen |
| arc | mätningen trasig (nämnaren = avsikt, inte svårighet) |

- [ ] Verifiera matchfördelningen: 1309 fuskare / 796 filer = 4,1 per match, vilket sitter
      illa mot README:s "317 med fuskare, 478 utan". `--labels` räknar nu det.

---

## XGuardian (USENIX Sec '26): råa CS2-demos finns — och wallhack.track är byggd baklänges

Pintuz vägrade acceptera "det finns inga andra källor". Han hade rätt.
https://arxiv.org/abs/2601.18068 | Zenodo: 13 poster, 2,8 TB | https://xguardian-anti-cheat.github.io/

```
#Match         2 903  (CS2CD: 795)
#Cheater         350  manuellt re-verifierade av 5EPlay (inte bara "VAC-bannad")
#Normal Player 5 136
Råa .dem-filer   ✅  → SpottedByMask INTAKT → wallhack äntligen testbar
```

### Typ-frågan är stängd för gott
> "Consistent with **industry standards**, labels are binary (i.e., cheater/normal player) and
> **do not distinguish between specific cheat sub-types**."

Ingen källa har fusktyper. Det är inte CS2CD som slarvade — så etiketterar hela branschen.
Retraktionen står, och inget dataset kommer rädda triggerbot-frågan.

### KRITISKT: wallhack-signaturen är STILLHET, inte tracking
> "in Case D, the cheater **only employs wallhack**. Legitimate players scan and react to visual
> cues. A wallhack user, however, knows an opponent's location in advance... This often manifests
> as **low-valued features just before an engagement; the cheater simply waits for the target to
> cross their pre-positioned crosshair.** XGuardian detects this **unnatural lack of movement**."

`wallhack.track` letar efter ett sikte som FÖLJER en fiende genom vägg. Det är motsatsen.
En wallhacker behöver inte leta — hen vet. Hon parkerar siktet och väntar.

Det förklarar varje observation vi gjort:
- track fyrar på stammisar → **de är de som faktiskt scannar och trackar**
- Pintuz flaggas 2x → spelar mest → rör siktet mest
- 0 fynd på 6,1M skott → vi letade efter rörelse hos folk som sitter still

Och Pintuz sa det själv idag ("om nån pre-firar precis när nån tittar fram") — sen byggde jag
en grind som kastade bort exakt de skotten för att de såg legitima ut.

**Nolltestet mäter rätt sak av en slump:** ett parkerat sikte på en vanlig vinkel korrelerar
lika med fiendens position då som nu. Ett parkerat sikte på RÄTT ställe gör det inte.
Nolltestet kräver ingen rörelse — det samplar varje tick.

- [ ] Ladda ner en delmängd XGuardian-demos (2,8 TB totalt — börja med en Zenodo-del)
- [x] ~~Kör nolltestet mot verifierade fuskare~~ → gjort på deras **feature-CSV** (ingen demo behövdes), se nedan
- [ ] Bygg om `wallhack.track`: **stillhet PÅ FAKTISK FIENDE**, inte stillhet ensamt (se nedan)
- [ ] Deras premiss är ett case study (n=1, expertkurerat). Stark ledtråd, inte bevis. Testa den.

---

## Nolltest-resultat på XGuardian:s etiketterade CS2-features (2026-07-16)

XGuardian-paketet är **inte** demos — det är feature-/kod-artefakten (0 st `.dem`; 862 CSV ~60 GB,
120k SHAP-PNG, tränade modeller, ett git-repo). Men det räcker: `Design/dataset/Train_cleaned.csv`
(162 MB) bär per-tick-features MED etikett. **Vi behövde aldrig demos för det här** — XGuardian
bygger allt på **pitch/yaw**, som vi redan samplar server-side.

**Schema:** `steamid,tick,kill_num,match_id,isFiring,isKilling,x,y,in_crouch,is_walking,
is_strafing,weapon,distance,isCheater,velocity_x,velocity_y,acceleration_x,acceleration_y,
angle,angle_change`. Elimination-fönster = `(steamid,match_id,kill_num)`, **exakt 96 tick,
killen alltid på idx 63** (63 tick före, 32 efter). Train: 10 334 fönster, 1769 spelare
(96 fuskare / 1685 normala), 23% cheater-fönster. `x,y` = 2D-skärmprojektion, kill-punkt ≈
mitten (962, 527); spridning x=549 men y=50 (aim är horisontell).

**KÄRNFYND — naiv stillhet är dödfödd, konjunktionen lever (spelar-nivå-AUC, low→cheater):**
```
Låg aim-hastighet ensamt ............ 0,457   ❌ FEL RIKTNING (stillaste = campers, renare än snitt)
Nära målet ensamt ................... 0,520
Låg hastighet + nära (additivt) ..... 0,495   ❌ brus
Låg hastighet × nära (produkt/AND) .. 0,684   ✅ CI [0,645, 0,720], konstant-grid 0,63–0,70
```
Det är **konjunktionen** som bär: ett still sikte är bara misstänkt om det sitter *på den faktiska
fienden*. Additivt (ELLER) = brus; multiplikativt (OCH) = signal. Det är exakt Case D
("waits for the target to cross their pre-positioned crosshair") = **vårt nolltest**.

**Brasklappar:** (1) bara train-spliten — Test/Val ej körda (håll-ut kvar). (2) Binariserad
konstant-fri variant ger bara 0,55 → signalen är mjuk, encoding spelar roll (därför XGuardian
lär hela trajektorie-formen, inte en skalär; 0,97 vs vår 0,68). (3) fuskarna är aimbot+wallhack
blandat men aimbot-flick-svansen syns INTE här (höga deciler tomma på fuskare) — datan är
stillhets-dominerad.

**Konsekvens för ombygget:** `wallhack.track` ska mäta **"pre-aim på faktisk fiendeposition
medan siktet står still", i pre-kill-fönstret** — INTE lågt aim-rörelse ensamt (floppar, AUC 0,46)
och INTE bearing-following (motsatsen). På live-server vet vi fiendens position → portbart.

- [ ] Härda: kör test+val-spliten, variera immediate-fönstret (idx 43–63), bekräfta 0,68 håller
- [ ] Läs `Design/src` + `Preporcessing` (fönster-semantik) och `5.3_Feature_Ablation` (vad bär mest)

### Port till DemoReplay + test mot wallers (2026-07-16) → nolltestet vinner

Byggde `killWall/killStill/killOnTgt` i DemoReplay: kill-ankrat pre-kill-fönster, stillhet ×
närhet till faktiskt offer, + LOS-grind (buffrad `SpottedByMask`, räkna bara ticks där offret
var OSPOTTAT av angriparen). Körde mot tre bannade (identiteter i private/ban-analysis.md) hämtade
från oldswedes-arkivet.

**killWall separerar INTE våra fuskare** — de hamnar 36/49, 29/49, exkluderad; toppen är
skickliga stammisar. Två orsaker: (1) per-kill n=3–17 är brus, och
(2) utan present-vs-past-kontroll fångar "still + på-mål" bara **bra pre-aim**.

**Det BEFINTLIGA nolltestet (unseenNow − unseenPast) rankar dem 1/70, 2/70, 8/70.** Det har
exakt de två sakerna porten saknar: present-vs-past-kontrollen (neutraliserar game sense) och
tusentals per-tick-sampel. Slutsats: **XGuardians värde för OSS är bekräftelsen att pitch/yaw +
en kontroll funkar — inte att ersätta nolltestet.** För vår rikare data (pos + spotted) ÄR
nolltestet det starkare instrumentet.

Brasklapp: n=3, överskotten små (+0,012), Pintuz (legit) ligger rank 3 tätt bakom. Sluta trimma
mot 3 fuskare (dokumenterad fälla) — skaffa riktig n innan mer portande.

- [x] ~~Promota nolltestet till en LIVE-detektor~~ → `NullTestDetector` (`wallhack.nulltest`) byggd,
      config-tunbar (`NullTestExcessThreshold` start 0 → höj tills stammisar faller ur = baseline)
- [ ] Ev. kombinera: kill-ankra/stillhets-grinda nolltestet (present-vs-past PÅ pre-kill-fönstret)

### Ban-list-sample + typning av fuskare (2026-07-16)

> Identiteter (SteamID, namn) och per-spelare-typning ligger i **`private/ban-analysis.md`**
> (gitignorerad — tredjepartsdata publiceras aldrig). Här bara metod + aggregat.

Etikett-semantik (från ägaren): permanent ban = fusk-policy, TEMP = ej fusk; "Hacking" =
multihack (wall+aim); "Other" = otypat (griefing/röst ELLER fusk). Demos hittas via ban-tidens
lokala tid → demon som startar strax före. En bannad joinar ofta sent, så tidigare-kväll-demos
hjälper bara om spelaren var med.

Poolat per spelare över 11 demos, percentil mot 76 legit-spelare (hit% p99=0,26, hs% p99=0,35,
nullExc p99=0,0037). **Nyckelfynd:** (1) de två axlarna SEPARERAR typ — hs%/hit% = aim,
nullExc = wall; en Other-ban var ren aim (lågt nolltest), en multihack-ban toppade båda.
(2) Den enda well-samplade säkra fuskaren toppar hela populationen på båda axlarna → detektorerna
fyrar på rätt axel. (3) en temp-ban låg i aim-botten → bekräftar temp≠fusk. **Tak:** bara en
säker fuskare blev well-sampled; resten joinade sent (n=1 solid) + flera trasiga stub-demos.

- [ ] För riktig n: storskalig replay + `BanCheck` (Steam ban-API) för auto-etikettering — enda
      vägen förbi "fuskaren joinade sent och spelade 1 min"
- [ ] Ev. dra tidigare demos för otypade Other-bans för att härda typningen
