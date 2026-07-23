# Admin-guide: granska en flaggad demo

> **Den enda regeln:** en flagga är en **pekare, aldrig en dom.** Verktyget säger
> "titta här" — *du* avgör. Ingen bannas på en siffra. Du är domaren, inte pluginen.

När OSAntiCheat flaggar något får du tre saker: **demo-fil**, **tick**, och **spelare**.
Den här guiden tar dig från det till en bedömning på ~2 minuter.

---

## Steg 1 — Öppna demon och hoppa till ögonblicket

Lägg demo-filen i CS2:s demo-mapp (`.../game/csgo/`) eller ange full sökväg. I spelets
konsol (` ~ `-tangenten):

```
playdemo 20260718-214640-de_nache        // ladda demon (utan .dem)
demoui                                    // öppna kontrollpanelen (play/paus/scrub/fart)
demo_gototick 43021                       // hoppa till den flaggade ticken
```

Klistra in de här en gång så blir granskningen snabb (binds funkar utan `sv_cheats`):

```
bind "p"          "demo_togglepause"          // paus/spela
bind "LEFTARROW"  "demo_timescale 0.1"        // slow-motion (10%)
bind "RIGHTARROW" "demo_timescale 1"          // normal fart
bind "x"          "toggle spec_show_xray 0 1" // röntgen av/på  <-- viktigast
```

**Tips:** hoppa till *strax före* ticken (t.ex. `demo_gototick 42800`), sätt `demo_timescale 0.1`
och titta på upptakten i slow-motion. Ögonblicket självt är för snabbt i realtid.

---

## Steg 2 — Den viktigaste knappen: röntgen (`spec_show_xray 1`)

Detta är hela nyckeln till wallhack-granskning. Röntgen visar **var fienderna faktiskt var**,
genom väggar. Nu kan du svara på den enda frågan som betyder något:

> **Kunde den misstänkte se/veta var fienden var — eller inte?**

- Sikte som ligger exakt på en fiende **du (via röntgen) ser bakom en vägg**, som spelaren
  omöjligt kunde se → misstänkt.
- Sikte som råkar peka mot en **synlig** fiende, eller mot en vanlig vinkel där ingen fiende är
  → normalt.

## Steg 3 — Titta från den misstänktes ögon

Klicka på den misstänktes namn (eller cykla spelare) så du ser **hens POV**. Kör upptakten i
slow-motion med röntgen på. Du letar efter *avsikt*: rörde sig siktet mot en osedd fiende, eller
pekade det bara i en riktning?

---

## Steg 4 — Vad betyder flaggan? Två nivåer

Flaggan kommer märkt med en nivå. De kräver olika saker av dig:

### 🔴 TIER 1 — LOGIC BREACH (bortom mänskligt)
`bone-lock`, `anti-recoil`. Handen **kan fysiskt inte** göra det här — ingen människa, inte ens
proffs. Hög konfidens. **Men bekräfta ändå** att det inte är en mätartefakt (se "spawn-stack"
nedan) innan action. Detta är den enda nivån där hård respons är motiverad.

### 🟡 TIER 2 — REVIEW FLAG (osannolikt, inte omöjligt)
`deadaim`, `null-test`. En människa **kunde** ha gjort det — en tursam hold, en bra läsning.
**Aldrig auto-ban.** Din uppgift: titta på klippet och avgör om det ser mänskligt ut. Och kom
ihåg — **en enda händelse är aldrig bevis.** Det som fäller är *upprepning*: samma spelare, samma
mönster, flera kvällar.

---

## Steg 5 — Per detektor: fusket vs dess legitima tvilling

Detta är kärnan. Varje flagga har ett **fällande** mönster OCH en **oskyldig** förklaring. Din
jobb är att skilja dem. Ser du den oskyldiga versionen → säg **nej**.

### `deadaim` — det parkerade skottet
| Fusk ser ut som | Legit tvilling |
|---|---|
| Fruset sikte parkerat på en **osedd** fiendes exakta position, avfyrar när hen anländer, laget såg hen aldrig, tyst | Höll en **vanlig vinkel** (dörr, choke, rök-utgång) och en fiende råkade gå in i den; hörde fotsteg; en lagkamrat callade |
| **Kolla:** var vinkeln udda (bara den fienden) eller en spot folk generellt håller? Röntgen: kunde hen hört/sett hen nyss? | Vanlig hold-spot + ljud/callout → **frikänn** |

### `bone-lock` — omänsklig sikt-precision
| Fusk | Legit tvilling / artefakt |
|---|---|
| Siktet snäpper **exakt** på huvudet, skott efter skott, noll spridning | En enstaka perfekt träff (händer på slumpnivå ~0,2%); **spawn-stack** (spelare fast i varandra på småkartor → falska nollor) |
| **Kolla:** är det UPPREPAT (3+) och på **verkligt avstånd**? | Stod de inuti varandra vid rundstart? → **artefakt, frikänn** |

### `anti-recoil` — omänskligt jämn rekyl-kontroll
| Fusk | Legit tvilling |
|---|---|
| Spray-mönstret är **identiskt** varje gång, missar aldrig | Skicklig spelare — men hens spray **varierar** spray-till-spray, missar ibland |
| **Kolla:** ser du variation mellan sprayerna? | Variation → människa → **frikänn** |

---

## Steg 6 — Domen: fyra utfall (inte två)

Tvinga aldrig ett binärt val. Använd fyra:

- **Fusk** — mönstret är tydligt, den legitima tvillingen passar inte, (helst) upprepat.
- **Misstänkt** — ser skumt ut men inte säkert; **behåll på bevakning**, samla fler kvällar.
- **Ren** — den oskyldiga förklaringen passar bättre.
- **Otillräckligt** — går inte att avgöra (för lite att se, kort klipp). Detta är ett **giltigt**
  svar — tvinga inte fram ett "ren" på tveksamt underlag.

---

## Gyllene regler

1. **Flagga ≠ dom.** Du bekräftar eller frikänner; pluginen anklagar aldrig.
2. **En händelse fäller aldrig.** Upprepning är det som skiljer tur från fusk.
3. **Granska blint när du kan.** Titta på *beteendet* innan du kollar *vem* det är — "det är ju
   bara [stammis]" är bias som gör dig blind.
4. **Vid tvekan: frikänn eller bevaka.** Vi hellre missar tio än sätter dit en. Ett felaktigt ban
   dödar förtroendet för hela systemet.
5. **Skicklig ≠ fusk.** De bästa spelarna ser bra ut på allt. Bara det *omöjliga* (Tier 1) eller
   det *oförklarligt osannolika + upprepade* (Tier 2) räknas.
