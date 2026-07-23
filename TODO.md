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
Med `--min-shots 50` såg listan lovande ut (`regular_A` 94 ms / 27,7% mot referensens 375 ms / 6%).
Men listan sorterade i praktiken på **stickprovsstorlek** — alla i topplistan hade 55–330 skott.
Med `--min-shots 2000`: hela spannet blir **234–375 ms** och referensspelaren hamnar mitt i klungan.
Ingen separation. (`regular_A` är gammal stammis som spelar sällan — 130 skott.)

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

Brasklapp: n=3, överskotten små (+0,012), en stammis (legit) ligger rank 3 tätt bakom. Sluta trimma
mot 3 fuskare (dokumenterad fälla) — skaffa riktig n innan mer portande.

- [x] ~~Promota nolltestet till en LIVE-detektor~~ → `NullTestDetector` (`wallhack.nulltest`), v0.6.0.

### v0.6.1 — nolltestet blir ett McNemar z-test

Live-data (storspelskväll) visade att rå excess var (a) brusigt vid låg N — toppar på +2–6pp över
1000 samples var ren slump och konvergerade till 0,1–0,5pp när N växte — och (b) ackumulerade
score → hela populationen på Review, dvs en speltids-mätare igen. Fix: **McNemar** z = (b−c)/√(b+c)
över de diskordanta sample (present-hit-ej-past `b` vs past-hit-ej-present `c`). Skicklighet
cancelar (concordant räknas ej), själv-kalibrerande (z≥3 ≈ 99,9% att asymmetrin ej är slump), ingen
speltids-förväxling (noll-effekt → z≈0 oavsett speltid), eskalerings-gate mot spam. Config:
`NullTestMinObservations`=30, `NullTestMinZ`=3.0. 4 nya enhetstester.
- [ ] Kör 6.1 live → bekräfta att stammisarna håller sig under z-tröskeln (aggressiva positionerare
      med äkta liten effekt kan eskalera långsamt → effektstorlek + korroboration avgör)
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

## Prior-art-genomgång (2026-07-17): öppna trådar att gräva i

Full granskningslogg av gamla anticheats i **[docs/prior-art.md](docs/prior-art.md)** (publik).
De flesta gamla plugins bygger på Source 1:s usercmd-ström (per-command angles/buttons/mouse/
tickcount) som CS2/CSSharp inte exponerar — så de flesta idéer är beundransvärda men oporterbara.
Det som faktiskt är värt att jaga vidare, prioriterat:

- [ ] ⭐ **Hallucination / phantom-entity — aktiv wallhack-DETEKTION.** Enda beteendemässiga
      wallhack-*detektorn* i hela HL/Source-linjen (alla andra "anti-wallhack" är occlusion/culling).
      Servern spawnar en fejk-fiende där bara ett fusk kan uppfatta den (i vägg/rök/ovanför) och
      mäter om spelaren snappar/skjuter. *Aktiv* — skapar observationer i stället för att vänta,
      angriper vårt n-problem direkt. **FEASIBILITY FÖRST:** kan CounterStrikeSharp injicera en
      server-spårad entitet som är dold för legit sikt? + läs [Activisions writeup](https://www.activision.com/cdn/research/hallucinations)
      och [arXiv 2409.14830](https://arxiv.org/pdf/2409.14830) för auktoritativ mekanik (CS 1.6-
      originalen var delvis rekonstruerade). Arms-race-varning: sofistikerade cheats filtrerar phantoms.
- [ ] ⛔ **ConVar-queries (från LAC/Lilac) — NEDGRADERAD, troligen inte värd det.** Idén: fråga
      klientens graphics-cvars server-side (`r_drawothermodels`, `mat_wireframe` m.fl.). **Två
      dödsstötar:** (1) *föråldrad attack* — de flesta cvarsen är `sv_cheats`-skyddade (gör inget på
      en normal server), och moderna fusk rör inte cvars alls; de läser minne + ritar egen ESP-overlay.
      (2) *trivialt spoofbar* — svaret kommer från klienten, ett hookande fusk returnerar rena default-
      värden och "inget svar = skyldig" ger FP på legit klienter med nätstrul. Fångar bara den lataste
      config-"cheat"-tiern. Behåll längst ner som ev. billig bottenskrap, men förvänta dig inget mot
      riktiga hot.
- [ ] 🟡 **Silent-aim via view-vs-shot-vinkel-mismatch (från StAC).** Skillnad mellan rapporterad
      vy-vinkel och faktisk skott-vinkel = silent-aim. Signal vi INTE har. **FEASIBILITY:** ger CS2
      oss båda vinklarna per skott?
- [ ] 🟢 **Omöjliga vinkelgränser (från LAC): pitch >89° / roll >50°.** Nästan gratis, deterministiskt,
      ingen legit klient sätter det. Lågt hängande — kan byggas oavsett de andra.
- [ ] 🟢 **Spin sensitivity-gate (från SMAC).** Gata vår spinbot på `sensitivity ≤ 6` för att inte
      fånga legit high-sens-flicks.
- [ ] 🔵 **Distributions-baserad flaggning (från Oryx-AC).** Bygg per-spelar-*fördelningar* av ett mått
      och flagga omänsklig *tighthet* i stället för att mönstermatcha en enskild händelse — närmast vår
      egen ansats. Metodik att studera (de riktar mot rörelse/strafe, vi mot aim).
- [ ] 🔎 **ReAimDetector (CS 1.6/ReAPI) — riktad gräv.** Server-side aim-snap, men inget auktoritativt
      repo med dokumenterade heuristiker hittat än.
- [ ] 🔎 **VACnet (Valve, GDC 2018) — conceptual read.** Valves egen server-side deep-learning-aimbot-
      detektion på SAMMA sorts data som vi har. Closed source men metodiken är offentlig.
- [ ] 📖 **Feature-mall (yviler/cs2-cheat-detection).** LSTM på .dem: pitch/yaw + 1a/2a/3e derivatan av
      aim-vinkeln, kumulativ förflyttning, kill-fönster. + red-team-läsning ["Aim Low, Shoot High"](https://arxiv.org/abs/2004.12183)
      för att veta vilka vinkel-heuristiker humaniserade aimbots slår.

## ⭐ Detektor-koncept: `wallhack.revisit` (dubbelpeeken) — 2026-07-17

**Ursprung:** ground-truth-observation från en demo-granskning på det gamla communityt (~18 år
sedan, de_gambaru) som fortfarande är den mänskliga go-to-signaturen för att spotta en wallhacker.
En still, dold fiende i ett hörn (unspotted, ingen LOS, inget ljud). Fuskaren i mitten:
la siktet **rakt på den dolda kroppen genom väggen** → gled bort åt vänster (flank-koll) → la
**tillbaka siktet exakt på honom igen** genom väggen → gick in efter. Ingen snap, inget skott
genom vägg. Långsamt/medvetet, 1,5–3s.

**Signaturen som ska detekteras:** vyn konvergerar på en still, osedd fiendes *faktiska* position →
**lämnar** (vinkelfel växer, glansar bort) → **åter-konvergerar på samma position** — som en episod,
medan fienden förblir unspotted hela tiden. Kärnan är **återbesöket**, inte en enda pre-aim.

**Varför återbesöket bär signalen (= nolltestets logik som en episod, inte ett aggregat):**
- EN crosshair-på-hörnet = håller en vinkel → oskyldigt, exculperande.
- Lämna och **återvända precist till samma dolda punkt** går inte att förklara med "höll vinkeln"
  (han lämnade den), och bara med game sense om punkten är *förutsägbar*. Off-angle hörn → ingen
  laglig informationskälla → nästan omöjligt att attribuera till skicklighet.
- Två oberoende on-target-händelser på en osedd, still, icke-uppenbart placerad fiende är
  astronomiskt osannolikt av slump — mycket starkare än en enda pre-aim.

**Fyller ett hål:** `gaze`/`track` kräver att fienden RÖR sig (vyn följer bäring) → fyrar inte på en
still fiende. Dubbelpeeken fångar precis det fallet. Rimmar med XGuardian-fyndet "signaturen är
STILLHET, inte tracking" + "still-aim × on-target", plus en temporal skärpning (återbesöket) som
slår FP:n "skicklig regular pre-aimar en vanlig vinkel".

**Annan sorts output = bevis en människa kan granska.** Nolltestet ger ett z-tal; dubbelpeeken ger
ett *klipp* ("12:03 — sikte på dold kropp, blick bort, sikte tillbaka"). Guld för admin-review och
en oberoende fusions-axel (still + episodisk + återbesök → korroborerar utan att överlappa våra andra).

**FP-historia — vad som avgör om den fyrar rätt:**
- ⚠️ **Off-angle vs vanlig hold-spot:** återvänder siktet dit fienden *faktiskt* är, vs dit spelare
  *i allmänhet* pekar i området (populations-relativ baslinje — INTE hårdkodade spots). Off-angle +
  återbesök = dödsstöt; vanlig pre-aim-spot = tvetydigt → måste hålla käft.
- ⚠️ **Audibility/callouts:** stod han verkligen tyst? Nyss skjutit/sprungit, eller lagkamrat-callout
  som gav positionen → exculperar (team-level-information, vår princip).

**Mätning:** episod-detektor, inte per-tick-tröskel — mönstret "on-target → off-target → on-target
på *samma* dolda punkt", inte bara lågt vinkelfel. Still-krav på fienden (låg velocity) + unspotted
(SpottedByMask) hela fönstret + off-angle-gate mot baslinjen.

- [ ] Retroaktivt testbart FÖRST i `DemoReplay` mot etiketterade fuskare — leta revisit-signaturen
      i efterhand innan något byggs live.
- [ ] Definiera on/off-target-trösklar (cone-radie) + återbesöks-fönster (~1,5–3s) + still-tröskel.
- [ ] Off-angle-gate: krävs populations-baslinje för "vart pekar folk här" innan den kan skilja
      återbesök-på-faktisk-position från återbesök-på-vanlig-spot.

## ⭐ Detektor-koncept: soft-aim — 2026-07-17 (forsknings­förankrat)

Soft-aim = "smooth aimbot": knuffar vyn mot fienden mjukt så det ser ut som spelaren rör siktet
själv (key-triggat). Inställbar mjukhet + hur nära den landar. **Två användningsfall:**
- *Kill/aggressivt* — knuffar mot fienden i en duell.
- *Information* (proffs-skandalens variant, virus på mus/matchdatorer): knuffar mot **osedda**
  fiender (bakom vägg/annan del av mappen); poängen är inte kill utan att spelaren *läser
  riktningen på knuffen* → vet var fienden är. Det gör det till ett **wallhack-informationsläckage**,
  inte ett aim-problem.

### Rätt ram: jaga informationen / motionen, inte "mjukheten"

Mjukheten är ratten fusket tunar för att se mänsklig ut → jaga inte den. Två oberoende axlar:

**A) Informationskanal (för info-varianten) — samma som nolltestet.** Frågan är inte "hur bra
aimen är" utan "**bär vyns riktning information om osedda fienders position den inte borde ha?**".
Info-soft-aim behöver inte *landa* på fienden — den bara *biasar bäringen mot* den. Så en
on-target-detektor missar den; en detektor på **bias-mot-bäring för *present* osedd position vs
*past*** fångar knuffen. Subtil men *ihållande* → ackumuleras statistiskt (per-tick-statistik slår
mänsklig granskning här — motsats till dubbelpeekens data-svält). Spotted-gate = FP-filtret
(knuffar mot *synliga* fiender = bara aim-kvalitet, irrelevant).

**B) Kinematik (för alla varianter) — "onaturlig jämnhet", INTE "konstant hastighet".**
⚠️ **KORRIGERING av första gissningen (konstant-hastighets-platå):** research (2026-07-17) föll den
som *specifik* signal av två skäl: (1) vanligaste soft-aimet = "flytta bråkdel av kvarvarande delta
per tick" = exponentiell ease-out (EMA-lågpass) → **front-laddat, ingen platå**; platån finns bara i
"clamped max turn-rate"-underfamiljen. (2) Människor **håller** nära-konstant hastighet vid *tracking*
(smooth pursuit ~konstant-hastighets-servo; steering law) → rå platå-detektor falsklarmar på legit
tracking av strafande fiende.
Den robusta separatorn är **jämnhet/mikrostruktur:** människor kan inte röra sig långsamt-OCH-jämnt
(Park & Hogan 2017, *"Moving slowly is hard for humans"* — under ~3s/cykel fragmenteras rörelsen i
2–5 delrörelser); mänsklig rörelse bär alltid 2–10 Hz delrörelse-rippel + 8–12 Hz darrning. Tellen
är **"för jämn, för repeterbar, rippel-frånvarande"**. Features: **SPARC / log-dimensionless-jerk** på
hastighetsserien, frånvaro av 2–10 Hz-rippel, låg varians mellan engagemang.
- **Amplitud-gate (användarens skärpning, håller):** *stor* jämn rörelse (45°+) är mycket mer omänsklig
  än en liten — över 45° skulle en människa definitivt visa homing/korrigerings-struktur. Stor-OCH-jämn
  > jämn ensam. OBS: 45°+ luktar *kill/aggressivt* soft-aim; info-varianten knuffar mindre.
- **Flick-gate:** kör bara i flick-regimen, INTE under tracking (annars FP på smooth pursuit).
- ⭐ **Self-normaliserad kontrast (kärnan):** poängsätt INTE svängens jämnhet mot en *global* tröskel
  (varierar per spelare/hårdvara; humaniserat soft-aim tunar mot den). Poängsätt den mot **samma
  spelares egen omgivande motorik, sekunder isär** — svängen är fällande för att den är omöjligt jämn
  *relativt hur den handen jittrar strax efteråt*. Cancellerar skicklighet/hårdvara (samma trick som
  nolltestets past-control) och är svårare att humanisera (att gömma svängen kräver att de mänskliga
  delarna görs lika jämna → återinför detekterbarhet).

### Incident-modell: maskin→människa→människa (episod, inte per-tick)

En *soft-aim-incident* = en **episod** med formen `[omöjligt jämnt segment (maskin)]` → `[jitter:
desorienterad människa söker sin sync, ~0,5–3s]` → `[muslyft/frys: om-centrering]` → synk igen.
Kontrasten *mellan* delarna är beviset, inte någon del för sig. Episod-detektor (som `wallhack.revisit`).

⚠️ **"Deterministiskt" gäller kausalkedjan (fysik), INTE manifestationen.** Lyftet kommer inte alltid
(små desyncar absorberas i jittret); jitter-längden varierar; info-varianten svänger litet (ingen
45°+ maskin-del); humaniserat smetar ut del 1. → **strukturell signatur poängsatt sannolikt**, aldrig
krav på alla tre.

**De 3 delarna är INTE 3 jämnstarka detektorer** — det är **1 bärande + 2 kontext:**
- **Del 1 (maskin)** bär hela diskrimineringen (det enda en människa inte kan göra).
- **Del 2 & 3 (människa)** är universella på egen hand (alla jittrar/fryser) → värde ENDAST som
  kontrast till del 1 (self-normalisering ovan) + temporal korroboration.
- ❌ **Hård AND** ("kräv maskin OCH jitter OCH frys") = SÄMRE: kostar recall (lyftet kommer inte alltid)
  och de mänskliga delarna separerar inte på egen hand.
- ✅ **Viktad fusion:** primär­axel = **self-normaliserad maskin-sväng** (bär signalen); jitter→frys-
  diskontinuiteten + spotted-gate + nolltest = **korroborations-bonusar** i `SuspicionEngine` (finns →
  höj konfidens; saknas → straffa inte).
- 🔒 **Ankaret:** bevisa att del 1 (self-normaliserad jämnhets-sväng) separerar fuskare från bästa
  legit-regulars i `DemoReplay` FÖRST. Jitter→frys-kontrasten testas som *tillägg* först därefter.

### Feasibility & caveats

- ✅ **Datan finns redan:** vi samplar pitch/yaw per tick (`TickSample`) → hastighets/accel-serien är
  rekonstruerbar; retroaktivt testbar i `DemoReplay`. Inte data-blockerad (till skillnad från phantom/silent-aim).
- ⚠️ **64-tick-verkligheten:** hastighet OK, acceleration marginellt (kräver Savitzky-Golay-utjämning),
  **jerk opålitligt** (brus × ω³ + mikrostruktur nära/bortom 32 Hz Nyquist + vinkel-kvantisering).
  128-tick ~fördubblar bandbredden, märkbart bättre. Behandla shape-features som grova ML-features (som
  yviler), inte ren fysik. Sub-tick hjälper INTE (tidsstämplar skott, inte aim-vågformen).
- ⚠️ **Rå vs kvantiserad vinkel:** verifiera att `pawn.EyeAngles` ger rå klient-vinkel (kvantiserad,
  inte server-utjämnad) mot verklig demo innan vi litar på jämnhets-måttet.
- ⚠️ **Humaniserings-kriget:** WindMouse (fysik + stokastisk vind + jitter, byggt för bot-undvikande) och
  GAN-aimbots (["Aim Low, Shoot High"](https://arxiv.org/abs/2004.12183)) slår shape-detektorer. → höjer
  *kostnaden* för evasion, inte oslagbart; hör hemma i ensemble.
- 💡 **Underutforskat = möjlighet:** SPARC/jerk/jämnhet är moget i mus-biometri men knappt porterat till
  FPS-anticheat på server-vinklar → genuint nytt, som nolltestet.

- [ ] Testa jämnhets-features (SPARC / log-dim-jerk) retroaktivt i `DemoReplay`, flick-gated, mot
      etiketterade fuskare + bästa legit-regulars innan något byggs live.
- [ ] Verifiera rå-vs-kvantiserad `EyeAngles` mot en demo; mät om jämnhets-signalen överlever kvantiseringen.
- [ ] Mät mänsklig baslinje: producerar legit-spelare någonsin rippel-fria segment? (populations-first).

## ⭐ Delad primitiv: maskin-signatur / "motor-brus-frånvaro" över kanaler — 2026-07-17

**Generaliseringen (användarens):** soft-aim-jämnheten är ett specialfall. Den egentliga primitiven är
**"frånvaro av mänskligt motor-brus"** — och den dyker upp *överallt där kod styr en kanal i stället för
en hand*. Människan har irreducibel varians (delrörelser, darr, spray-till-spray-skillnad); koden
upprepar sig för exakt. Gemensam feature: **låg varians mellan instanser, self-normaliserat** (samma
röda tråd som nolltestet — jämför spelaren med en kontroll som cancellerar skicklighet, nu på *motoriken*).

**Kanaler den generaliserar över:**
- **View** (aim) — soft-aim, recoil-script (nedan).
- **Position** (strafe/bhop) — Oryx-AC gör redan detta ("för många perfekta strafes", varians ~0).
- **Timing** (eld-intervall, autoshoot) — "för regelbundet".

### ⭐ Starkaste + lättast-validerade tillämpning: recoil-control-script (testa FÖRE soft-aim)

Ett recoil-script rör vyn i ett precist skriptat mot-mönster under spray för att kansellera rekylen.
Bättre första mål än soft-aim av tre skäl:
1. **Maskin-genererad view-rörelse** → har maskin-signaturen.
2. **Extremt repeterbart** → self-normaliseringen blir kristallklar: människans rekylkontroll *varierar*
   spray-till-spray; scriptets är **identiskt varje gång**. "Låg varians mellan sprayer" > absolut tröskel.
3. **Sprayen segmenterar rörelsen GRATIS** — löser det svåra onset/offset-problemet (vi vet när spelaren
   eldar); spray-fönstret *är* rörelsefönstret.

### Varför generaliseringen är värdefull (inte bara fler idéer)

Mät-infrastrukturen (jämnhets/varians-mått + baslinje offline i `DemoReplay`) är **delad, inte
engångs**: samma maskineri som baseline:ar aim-jämnhet baseline:ar recoil-script-jämnhet och
strafe-regelbundenhet. Investeringen i mät-lagret betalar sig över flera fusktyper → starkare case för
att bygga det.

**Ärliga gränser (samma som soft-aim):** familj/princip, inte en detektor — varje kanal behöver egen
segmentering + baslinje + positiva prov; humaniserade script lägger till jitter → fångar naiva, inte
bäst-tunade; 64-tick-brusgolvet gäller överallt.

### Arkiv-mining som discovery (utökar n=3) — MEN cirkularitets-grinden är obligatorisk

Samma offline-maskineri kan **skanna hela arkivet** (17k demos) efter maskin-rörelser → angriper
n=3-problemet genom att *hitta* okända fuskare. ⚠️ **En detektion är en MISSTÄNKT, inte en bekräftad
fuskare.** Att validera detektorn på de demos den själv pekade ut = **cirkulärt** (samma fälla som
"sweeping configs mot få positiva hittar slump-vinnare"). Discovery får aldrig vara sin egen ground-truth.

**Varje kandidat kräver *oberoende* bekräftelse innan den räknas som positiv:**
- **Ban-listan** (`BanCheck` / `sa_bans`) — bannad oberoende av admins = riktig ground-truth.
- **En oberoende axel** fyrar också (nolltest, hit%) — oberoende bevis, inte samma mätning två gånger.
- **Manuell demo-granskning.**

**Outlier-iteration:** arkivet innehåller fuskare → de kontaminerar baslinjen (svansen). Bygg baslinje →
flagga svans-outliers → bygg om renare baslinje utan dem → upprepa.

**Renaste positiva = kontrollerade testservern** (garanterad ground-truth), inte arkiv-fynden. Arkiv-
discovery utökar verkliga positiva men alltid genom oberoende-grinden.

**Pipeline:** skanna alla → kandidater → oberoende-bekräftelse-filter → bekräftad delmängd utökar
etiketterad mängd → *sen* validera på held-out. Discovery matar etiketteringen, ersätter inte valideringen.

- [ ] Testa recoil-script-signaturen (varians mellan sprayer, self-normaliserat) i `DemoReplay` FÖRE
      soft-aim — sprayen ger segmenteringen gratis, "identisk mellan sprayer" är en tydligare tell.
- [ ] Bygg jämnhets/varians-mät-lagret som en *delad* primitiv (återanvändbar över view/position/timing),
      inte inbakat i en enda detektor.
- [ ] Arkiv-mining: skanna alla demos → kandidater, men grinda på ban-lista/oberoende-axel/manuell
      granskning innan något räknas som positiv. Aldrig cirkulär validering.

## ⭐ Recoil-axeln testad end-to-end + RÄDDAD som smal detektor — 2026-07-17

Byggde hela pipelinen och körde den på riktig data samma dag: `DemoReplay` recoil-ratio (cross-spray-
spridning ÷ pull; tapping uteslutet vid källan via kadens 0,13s + pull-gate ≥2°), `BanCheck`-recoil-
korsning, och `RecoilSynth` (syntetisk maskin-positiv, **inget fusk körs** — anti-recoil är deterministiskt).

**Real data (4776 demos, senaste året, 17k spray-sessioner):**
- Människo-golv: per-session **0,06** (ägaren Pintuz i botten!), per-spelare (median ≥2 sess) **0,21**.
- Jämn kontinuum, **inget separerat kluster**. Lägsta = skickliga regulars (bästa legit-kontrollen 0,09 osv).
- **Första (för hastiga) slutsats: "mäter skicklighet, dött"** — samma som dwell/target-switch/timing.

**Ban-korsning är OGILTIG för anti-recoil (VAC-oberoende insikt från användaren):** VAC fångar inte
macro/hårdvaru/externt anti-recoil → inga giltiga etiketter i arkivet. "Ingen ban-anrikning" betyder
"vet ej", inte "frikänd". Ban-datan dessutom mest 8–20 år gamla orelaterade VAC-bans. → arkiv+ban-
discovery är en återvändsgränd FÖR DEN HÄR fusktypen.

**RecoilSynth RÄDDADE axeln (mot min pessimism):** ett *perfekt* script → ratio **~0,00**, långt under
människo-golvet 0,06. Kvantisering försumbar. **Alltså är 0,06 äkta människo-motorbrus, inte mät-golvet
— det FINNS ett gap under människorna.** Humaniserings-kostnad: script måste injicera **≥0,15° per-skott-
jitter** för att nå 0,06 och gömma sig.
```
sigma=0(perfekt) 0.00 | 0.05->0.019 | 0.10->0.038 | 0.15->0.057(korsar) | 0.20->0.076
```

**REVIDERAD DOM — smal hög-precisions-detektor, inte död:**
- Tröskel **~0,04** = bortom mänsklig räckvidd (17k sessioner golvade 0,06) → ~noll FP.
- Fångar **ohumaniserat** anti-recoil (billiga script). Låg recall (missar ≥0,15° jitter), men
  **trovärdig när den fyrar** → förtjänar "anti-recoil"-etiketten där, för ingen människa når dit.
- Real data hade inget <0,06 → inga naiva scripters i fönstret (spelade ej, eller humaniserar).
- [ ] Ev. wire:a in som fusions-axel med tröskel ~0,04 (log-only), etikett "trolig anti-recoil" ENDAST
      under golvet. Mät verklig CS2-vinkel-kvantisering från en demo för att bekräfta q-kolumnen.

**Lärdomar (dagens):**
- **Testa innan du dömer — åt BÅDA håll.** Jag kallade axeln död för att människor når lågt; syntesen
  visade ett gap under dem. Kalibreringslabbet fångade *både* skicklighets-confounden OCH min för-hastiga
  dödförklaring.
- **Deterministiska fusk är syntetiserbara** → maskin-ankare utan att köra fusk / riskera VAC. (Wallhack
  är perceptions-baserat → behöver riktiga demos; anti-recoil är ren mekanik.)
- **Skicklighet vs bortom-mänsklig-precision:** recoil-*konsistens* mäter det skicklighet producerar,
  MEN en tröskel *under* människo-golvet separerar ändå den naiva maskinen (som är bortom mänsklig
  precision). Jfr nolltestet som separerar via *information* (ej köpbar med skicklighet).

**Design-princip: typade ledtrådar utan confirmation bias (skriv INTE bara "anti-recoil" till admin):**
- Larmet ska **lära admin skilja fusket från dess legit-tvilling** — visa BÅDE det fällande OCH det
  frikännande mönstret ("script = identiskt varje spray, missar aldrig; skicklig människa varierar,
  missar ibland — ser du variation → legit"). Ger admin ett sätt att säga NEJ.
- **Bevis, inte anklagelse:** larmet bär klippet/siffrorna + legit-mönstret, inte en dom att bekräfta.
- **Bara validerade axlar får en etikett.** Wallhack (nolltest) har förtjänat sin; anti-recoil förtjänar
  sin ENDAST under golvet ~0,04. Arkitekturen vet vilka axlar som fyrade → mappning axel→etikett naturlig.

## ⭐ wallhack.revisit (dubbelpeeken): DÖD som auto-dom, LEVER som misstänkt-lyftare — 2026-07-18

Byggd i `DemoReplay` (per (observer, osedd fiende): on-target-dwell → off >20° → on igen inom 3s, still
fiende), + `--revisit-detail <steamId>` som dumpar varje episod (tick+tid+fiende-pos) för granskning.

**Iteration + real data (4776 demos):**
- Rå kon-korsning fyrade på **89%** av sessionerna (gaze-problemet). Dwell-krav (parkera ~0,15s, inte
  svepa) → **44%**. Fortfarande på nästan hälften → för löst för en sällsynt cheat.
- Topp-rate = kort-session-inflation + skickliga regulars (samma namn som wallhack.track/recoil).

**Manuell granskning av topp-kandidaten (5,44/min, 2-min-session) — användaren tittade på demon:** FALSE
POSITIVE, väl diagnosticerad. 9 av 11 revisits var EN ~1,2s klung-situation (crosshair sveper en grupp
osedda fiender → detektorn dubbelräknar per fiende-par). Den isolerade "0,6°-låsningen" var: han stod
still, förde siktet fram/tillbaka (kartkoll, ny på mappen), en fiende *gick in* i det stilla siktet.

**Rotorsak (strukturell, inte en bugg):** på en rektangulär map med lagen på motsatta sidor står **hela
fiende­laget bakom en vägg** i tittriktningen (~20 spelare = ~10 fiender bakom väggar). "Sikta nära osedd
fiende" är då trivialt uppfyllt hela tiden → geometri × folkmängd ger 44% baslinje. Dwell/still räddar
inte en *geometrisk* confound.

**Varför nolltestet funkar men dubbelpeeken inte:** dubbelpeeken **saknar past-controlen.** Nolltestet
jämför fiendens present- mot past-position; en legit spelare som håller väggen är på båda lika (game
sense) → cancellerar, bara present-medan-osedd överlever. Dubbelpeeken räknar bara "siktade på osedd
fiende två gånger" → geometrin gör det universellt. Samma röda tråd som dödade recoil-konsistens och
timing: **det som separerar är kontrollen som cancellerar det legitima, inte signalen själv.**

**Dom:** dubbelpeek-auto-detektorn hyllad. Konceptet (granskbart klipp) lever — men klippen ska komma
från **nolltestets starkaste present-över-past-ögonblick** (ärver past-controlen), inte revisit. Ögat
kan spotta en avsiktlig dubbelpeek; vår statistik fångade geometrin, inte avsikten.
- [ ] Ev.: generera granskbara klipp ur nolltestets högsta present-over-past-ticks (tick+fiende-pos)
      via samma `--*-detail`-mönster som revisit-dumpen — DET ärver past-controlen.
- Detektor-lärdom att behålla: `wallhack.revisit` **dubbelräknar klungor** (per-par-fyrning). Om något
  liknande byggs igen: deduplicera episoder i tid per observatör, inte per fiende-par.

### ⭐ ÅTERUPPLIVAD som misstänkt-lyftare (2026-07-18) — omcentrering: misstänkt till admins, INTE auto-dom

Nyckel-insikten (användaren): målet är inte en perfekt statistisk separator — det är att **flagga en
misstänkt till admins.** Wallhack är subjektivt → MÅSTE granskas av människa → dubbelpeeken behöver inte
noll FP, den behöver vara **specifik nog att en träff är värd 30 sek granskning** + producera **klippet**.
Det var mitt "död"-utlåtande ovan som var för snabbt: dött som *auto-dom*, inte som *misstänkt-lyftare*.

**Fixen som gjorde den specifik:** dwell 0,15s → **~1s (20 polls) BÅDA parkeringarna, på SAMMA fiende**
(samma fiende var redan strukturellt via per-par-state). En 1s-parkering på en still dold fiende, två
gånger, är avsiktlig — svep/klungor/kartkolls-panorering kan inte hålla 1s. Fire rate: 89% → 44% (0,15s
dwell) → **~7%** (1s, litet lokalt urval) — sällsynt, granskbar volym, ~1 episod per lång session.

**Roll (korrekt tak per fusktyp):**
- Auto-ban: bara signaturer *bortom mänsklig räckvidd* (naivt anti-recoil <0,04, omöjliga vinklar).
- **Wallhack: misstänkt-lyftare + klipp → admin granskar → admin bannar.** Aldrig auto-ban på ett z-tal.

`--revisit-detail <steamId>` dumpar varje episod (tick+tid+fiende-pos) → admin hoppar dit i demon och gör
off-angle-bedömningen. **I rätt omständigheter (still fiende, off-angle, 1s+ parkering ×2 på samma fiende)
är detta ett av de bästa sätten att hitta en wallhacker** — ögat ser avsikten, verktyget hittar ögonblicket.
- [ ] Full-arkiv-körning för riktig fire rate (~7%?) + granska topp-träffarna (är de äkta dubbelpeekar?).
- [ ] Om volymen håller: wire:a in log-only som en wallhack-*misstänkt*-axel (aldrig auto-action), med
      klipp-dump i larmet (typad ledtråd + bevis, inte anklagelse).

## ⚠️ DAGENS STORA SYNTHES (2026-07-18): server-statistik har ett hårt tak för wallhack

Kalibreringslabbet mot 4772 riktiga demos + live-logg avslöjade gränsen för vad server-data kan.

**Meta-fyndet (det viktigaste):** **ägaren (Pintuz) toppar VARJE axel** — recoil (0,06), null-test (z=13),
follow (20s) — och de bästa legit-kontrollerna sitter i toppen på alla. Det är inte slump:
> **Skicklighet ⊇ de wallhack/aim-tells vi kan mäta server-side.** Aim-forward, håll-vinklar, pre-aim,
> spåra rörliga fiender — allt som avslöjar en fuskare avslöjar också en *skicklig* spelare, för det är
> samma beteende. Taket: de bästa legit ser ut som milda fuskare på varenda axel. Bara sällsynta,
> specifika EPISODER (för människo-granskning) separerar; inget auto-detekterar rent.

**Null-test läcker LIVE (flaggskeppet):** deployade `wallhack.nulltest` fyrar på hela stammis-basen
inkl. ägaren, z=5–13. Orsak: z = signifikans, växer med √(N); legit har en *verklig* present-över-past
(~0,55–0,75) från **aim-forward** (du pre-aimar dit fienden är på *väg*, = present, inte past). Past-
controlen cancellerar symmetrisk game sense men INTE riktad pre-aim. "z≈0 för legit" var fel antagande.
Offline-"valideringen" visade RANKNING (fuskare högre), inte en ren tröskel. Fix (oprövad): tröskla på
ANDELEN (effektstorlek) population-relativt över ~0,75, inte z — men gapet till fuskare är smalt/okänt.

**wallhack.follow (byggd idag, `DemoReplay`):** följ en osedd fiende ≥3s medan den rör sig. TVÅ buggar
hittade + fixade: (1) ackumulerade över spotted-luckor → 1500s "följningar" (fix: kräv kontinuitet); (2)
momentan-hastighets-gaten false-positivade på en **taser/eco-STANDOFF** (båda väntar vid en vägg) → 20s
"follow" (fix: kräv net-förflyttning ≥300u — *diagnosticerad av användarens spel-sense*). Ändå
confoundad: ägaren toppar, långa följningar har lågt svep (~radiella håll), och sweep-genom-svängar
BRYTER kontinuiteten strukturellt (när fienden svänger släpar siktet → err spikar → följningen bryts).
Så den fångar det confoundade (raka håll) och missar det diskriminerande (svängar).

**wallhack.revisit slutgiltig:** clutch(≤2) + tyst(≤140u/s) + 1s-lås(2,5°) + snabb(≤4s) + samma fiende +
djup → **5/år, 0 trippel-peekar**. Extremt sällsynt misstänkt-lyftare (ej auto); även de kan vara callouts.

**⭐ Demo-kompatibilitet (praktiskt, avgörande för produkten):** gamla demos blir OSPELBARA när Valve
uppdaterar CS2 → `playdemo` funkar bara på FÄRSKA demos. Konsekvens: klipp-granskning (slutmålet) funkar
bara nära live. **Pipelinen delas:** arkiv = KALIBRERING (baslinjer, funkar på gamla), live/färskt =
MISSTÄNKT-LYFTARE + auto-ticket (måste flagga medan demon går att titta på).

**Produkt-metrik-idé (perkulerar):** testa flera sätt att kvantifiera legit, ta 2 med OLIKA confounds,
normalisera till percentil, MULTIPLICERA (XGuardian-mönstret still×on-target). En legit hög på en men
normal på den andra → produkten dör; fuskare hög på båda → tänds. Kräver om-validering.

- [ ] Nästa: juli-endast-körning (`--since 20260701`) → hitta en FÄRSK (spelbar) demo med en follow/revisit-
      träff → titta på den i CS2 (den granskning gamla demos nekar oss). Sen ev. produkt-metrik-experimentet.
- [ ] Null-test: byt z→andel + population-relativ tröskel, om-validera mot etiketterade fuskare (eller
      erkänn att även den mäter aim-forward, inte information).

## ⭐ Detektor-koncept: `wallhack.deadaim` (det parkerade skottet) — 2026-07-18

**Ursprung:** ground-truth-granskning av en kvälls-demo 2026-07-18 (identiteter + full forensik i
**`private/deadaim-case-20260718.md`**, gitignorerad). Ägaren såg det live: en gäst-spelare **G**
(genomsnittlig hela mappen) drar ETT galet skott på stammis **K** genom en rök i runda 7.
`--revisit-detail` på G:s kills bekräftade och kvantifierade det.

**Skottet, rekonstruerat (runda 7, tick 43021, deagle HEADSHOT, ~1135u / ~22m):**
```
kill = HEADSHOT genom rök                   — frusna siktet låg på HUVUDHÖJD, ej bara silhuetten
crosshair FRUSET 2750ms  (<0,15°/tick)     — handen ligger still, nästan 3 sekunder
osedd av G  64/64 ticks                    — ingen egen LOS i uppspelet (rök)
osedd av HELA laget 0/64 ticks (team-saw)  — ingen lagkamrat hade ögon → ingen callout-källa
offret KRYPER in i linjen: 110u, net 110u  — rak, långsam, TYST rörelse (mindre ljud att reagera på)
aimErr 2,7° → 0,1° (t-94ms) → 0,5° @skott  — K gör hela konvergensen; G justerar inte (bitfruset)
skott ~94ms EFTER närmsta punkt              — mänsklig reaktionsbeat, INTE trigger (den fyrat vid 0,1°)
sig 0,060 → 0,239 efter acos-fixen          — rank 1/164 i demon på gatedSig, 4,5× marginal
```
G:s andra helt blinda kill (runda 16) var raka motsatsen: 75°/s spray, 18,9° fel. **Ingen
upprepning i den här demon** — n=1 event. Repetition kräver hans övriga (färska) matcher; han finns ej
i sample-korpusen (demos/).

**Konceptet:** diskret event = **fruset sikte (frys ≥ ~1s) + osedd av hela laget + offret rör sig IN i
linjen + off-angle mot baslinjen → kill.** Producerar ett klipp (tick + offer + trajektoria), korroborerar
mot nolltestet utan att överlappa (episod, inte per-tick-aggregat).

**Ärlig placering — detta är INTE en ny ren detektor.** Det är `killWall` (XGuardians still×on-target,
AUC 0,68) som redan visat sig **toppa på skickliga regulars** utan past-control (rad ~496). Det `deadaim`
lägger till är tre axlar porten saknade:
| Axel | Skottet | Ny mot killWall? |
|---|---|---|
| Team-info (såg NÅGON i laget offret?) | 0/64 | ✅ ja — callout-exculpation, vår team-level-princip |
| Offer-rörelse (rör sig offret in i linjen?) | kryper 110u tyst | ✅ ja — rörligt osynligt mål > parkerat på stilla |
| Frys-längd (avsiktlig park?) | 2750ms | ✅ ja — skiljer park från råkande svep |
| **Off-angle mot baslinje** | **okänt** | ⛔ **saknas — det avgörande** |

**Taket gäller ändå (2026-07-18-syntesen):** utan off-angle-grinden fångar "fruset + på-mål + osett"
bara **bra pre-aim på en vanlig lane** — och en skicklig regular (ägaren) gör det hela tiden. Skottet är
**observationellt identiskt** med en tursam common-angle-hold tills baslinjen säger att punkten var udda.
Off-angle-baslinjen ("vart pekar folk härifrån på de_nache", populations-relativ, ej handkodad) är samma
obyggda grind som blockerat revisit/track hela vägen. **Den är förutsättningen, inte en detalj.**

**Roll (per fusktypens tak):** wallhack = **misstänkt-lyftare + klipp → admin granskar → admin bannar.**
Aldrig auto-action, aldrig på ett event — bara på **rate över basraten** (som `wallhack.revisit` slutade).

**Redan byggt (instrumentering, `DemoReplay`):** `--revisit-detail <steamId>` dumpar nu varje kill med
`[KILL]`-rad (team-saw, offer-rörelse, frys-ms, positioner) + per-tick-trajektoria för de blinda. Räcker
för retroaktiv granskning; ingen live-kod ändrad.

- [ ] **Off-angle-baslinje FÖRST** (blockerar allt annat): populations-heatmap "vart pekar folk härifrån"
      per karta, så `deadaim` kan skilja fruset-på-faktisk-fiende från fruset-på-common-spot. Utan den: håll käft.
- [ ] Mät basraten: kör arkivet (kalibrerings-split), fördelning av frusna-blinda-on-target kills per
      alive-timme bland kända legit-regulars. Var landar G:s rate? (n=1 i en demo klarar aldrig baren.)
- [ ] Repetition på G: dra hans övriga FÄRSKA demos, kör `--revisit-detail <steamId i private-filen>`,
      se om parkerade-skott-mönstret återkommer eller om runda 7 är en ensam outlier (= slump).
- [ ] Om det överlever: wire:a log-only som wallhack-misstänkt-axel med klipp-dump, gated på rate +
      off-angle + gärna nolltest-korroboration. Färska demos only (klipp-granskning kräver spelbar demo).

## ⭐ Bone-lock / head-precision: SKILL-INVARIANT med hård kant — validerad mot proffs — 2026-07-22

**Idén (användarens):** "ingen kan klicka HS som en maskin" — ackumulera hur off HS-klick är från
huvudets mittpunkt; en average nära noll = maskin. Byggd som `headErr` i DemoReplay: vinkel från
**view-vektorn vid `WeaponFire`** (inte kulan — vapenspridning är brus cheatet inte styr) till offrets
huvudcentrum (feet+64), first-of-burst + on-target (≤5°). Ny kolumn i skott-exporten + per-demo-tabell.

**Mät-enheten är kvantiseringssteget 0,044°** (= 360/8192, uppmätt i deadaim-forensiken 2026-07-22:
råa demo-vinklar stegar exakt 0,044). En rå aimbot beräknar exakt bone-vinkel och kvantiserar →
**≤ ~0,022° varje låst skott — analytiskt, ingen synth behövs.** Människan siktar på ett huvud som
spänner ~100 kvantceller → sprids. Statistiken är **"spike-andel ≤0,05°"** (mixture-robust: en
togglares legit-skott kan inte späda ut spiken; average kan de lura — Oryx-tänket, fördelnings-form).

**Trepopulations-test samma kväll (~2 200 on-target first shots):**
```
                        medianer      p10          spikes (≥3 = maskin)
oldswedes stammisar(18) 0,93–2,31°   0,25–1,15°   0   (1 exakt-nolla/838 = slumpcell)
VP vs NaVi, tier-1 (10) 0,92–2,19°   0,27–0,65°   0   (2 singlar/852)
MOUZ NXT semi-pro  (10) 1,48–2,18°   0,18–0,87°   0   (1 singel/509)
```
**b1t — en av världens bästa huvudklickare — median 1,85°, mitt i vår stammis-klunga.** Terminal
klick-precision är motor-bunden ~1–2° median FÖR ALLA människor; proffs-skillnaden bor i allt annat
(placering, disciplin, movement) som on-target-villkoret normaliserar bort. Alltså:

> **Första axeln sedan recoil-variansen med HÅRD KANT i stället för skicklighets-gradient.**
> Skicklighet glider INTE mot maskin-zonen här — världseliten ≈ stammisarna ≈ samma puckel, och
> zonen ≤0,05° upprepat är TOM i alla tre populationerna. Enstaka exakt-träffar sker på slumpnivå
> (~0,2% av skotten, aldrig upprepat) — därför spike-ANDEL, aldrig enskilt skott.

Proffsdemos = perfekt negativ kontroll (extrema högersvansen av mänskligt): flaggar måttet proffs
→ det mäter skicklighet → dött; puckel utan spik → golvet överlever sitt hårdaste naturliga test.
Det överlevde. (Källa: demofile-net:s publika test-fixtures — VP–NaVi Ancient 2024, MOUZ NXT–Space.
HLTV är Cloudflare-blockerat från sandboxen; bo3.gg:s demos bakom inlogg.)

- [ ] **Definitivt golv: kör arkivet** (6,1M skott, `headErrDeg`-kolumnen finns nu i exporten) —
      var ligger populationens spike-andel? Förväntat ~0 utom slump-singlar. Sen tröskel under golvet.
- [ ] Verifiera kvant-steget på 128-tick-demos (proffsen) vs våra 64-tick — samma 0,044° eller finare?
      Påverkar var maskin-zonen slutar.
- [ ] Humaniserings-taket (ärligt): slumpad offset inom hitboxen slår spike-checken → då är detta
      "fångar de lata"-tiern + varians-backstopp (för-tight fördelning self-normaliserat) för resten.
- [ ] Efter arkiv-golvet: wire:a som **andra bortom-mänskligt-auto-axeln** (efter anti-recoil <0,04):
      spike-andel över tröskel = "trolig bone-lock aimbot", typad ledtråd + klipp, aldrig på ett skott.

## Data-inventering (2026-07-23): vad servern kan ge som vi inte tar

Fråga (ägarens): har vi allt CS:S/CS:GO-SourceMod hade? Nej — men det mesta av gapet går att stänga.

### Nivå 0 — FINNS REDAN i CS2/CSSharp/demos, oanvänt (gratis, börja här)
- [ ] **`bullet_impact`-eventet**: skottvektor (öga→nedslag) vs eye-vinkel = **silent-aim-detektorn**
      (TODO:ns gamla feasibility-fråga troligen redan löst). Finns i demos → retroaktivt testbar.
- [ ] **`player_footstep`-eventet**: RIKTIG hörbarhet i stället för fart-proxyn (deadaims ljud-grind
      blir exakt: fanns fotstegs-event eller ej). Finns i demos → retroaktivt testbar.
- [ ] **`m_flFlashDuration`**: flashad-accuracy-axeln — pricka någon MEDAN flashad = klassisk tell (SMAC).
- [ ] **`m_aimPunchAngle` + `m_iShotsFired`**: exakt recoil-ground-truth till anti-recoil-axeln.
- [ ] **`m_vecViewOffset`**: crouch-korrekt huvudhöjd → skarpare bone-lock (feet+64 mäter fel på hukande).
- [ ] **Smoke-projektil-entities** (pos+radie): genom-rök-grinden automatiskt (George-fallet ögonvittnades).
- [ ] **Bot-flagga på offer**: bot-övertagna offer (t.ex. disconnect-bots) är deadaim-bete (vandrar
      förutsägbart, tyst, osett) — exkludera som offer. Upptäckt: arkivets topp-hit var `-> X [Bot]`.

### Nivå 1 — Extension/bibliotek som redan existerar (installera/referera)
- [ ] **[CS2TraceRay](https://github.com/schwarper/CS2TraceRay)** (NuGet): world-trace för CSSharp →
      LIVE LOS + ljud-ocklusion + off-angle-geometri. Låser upp TODO:ns största tekniska risk.
- [ ] **Offline-LOS för DemoReplay** via ren Source2-kartparsning: [ValveResourceFormat/Source2Viewer]
      (https://github.com/ValveResourceFormat/ValveResourceFormat) (MIT, SteamDatabase) extraherar
      kollisionsgeometri ur `.vpk` → bygg BVH → raycasta. Ger off-angle-baslinjen utan live-server
      (arkivet kan geometri-analyseras retroaktivt). OBS: neutral map-parsing (samma teknik som radar/
      nav-verktyg) — INTE cheat-repon som paketerar "autowall"; ta tekniken, aldrig sån kod.

### Nivå 2 — Kräver hook-arbete (usercmd/subtick — största vinsten, mest jobb)
Source1:s `OnPlayerRunCmd` (rå per-command: knappar, vinklar, tickcount) var klassiska AC:ers ryggrad
(silent-aim/psilent, command-nivå-trigger, backtrack-detektion). CS2:s protokoll är RIKARE (subtick =
**ms-tidsstämplade inputs** → trigger/reaction-axeln 1ms-upplöst i stället för 15,6ms) men CSSharp
exponerar det inte färdigt. Vägar: (a) CSSharp:s gamedata-signaturer + MemoryFunction-hooks från C#
(så CS2TraceRay funkar internt; signaturer bryts vid CS2-uppdateringar), (b) liten C++-metamod-kompanjon
(CS2Fixes-stil) som matar CSSharp, (c) PR till CSSharp uppströms (bidrag > ask; hela AC-communityt vill ha).
- [ ] Inventera aktuell CSSharp-API/issues först — delar kan ha landat efter vårt kunskapsläge.

**Prioritet:** Nivå 0-freebies först (footstep + bullet_impact är demo-testbara utan serverändring) →
CS2TraceRay live + vpk-BVH offline → usercmd/subtick sist. Axel-mappning: usercmd/subtick matar
MEKANIK-cirkeln (trigger, silent-aim, recoil); trace/geometri matar INFORMATIONS-cirkeln (LOS, ljud,
off-angle) — båda Venn-halvorna får förstärkning.
