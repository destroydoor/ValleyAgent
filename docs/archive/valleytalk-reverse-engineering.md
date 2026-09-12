# ValleyTalk Mods - Complete Reverse Engineering Report

## Executive Summary

**ValleyTalk** is a sophisticated Stardew Valley SMAPI mod that generates infinite AI-powered NPC dialogue using Large Language Models (LLMs). It consists of:

1. **ValleyTalk Base** (v1.0.2) - Core C# mod + Content Patcher data pack with 33 NPCs
2. **ValleyTalk for SVE** (v0.1.0) - Content Patcher add-on adding 20 SVE NPCs

**Author**: dandm1 | **Nexus**: 30319 (Base), 34341 (SVE)

---

## Table of Contents

1. [Architecture Overview](#1-architecture-overview)
2. [ValleyTalk Base - DLL Analysis](#2-valleytalk-base---dll-analysis)
3. [ValleyTalk Base - Data Architecture](#3-valleytalk-base---data-architecture)
4. [ValleyTalk for SVE - Add-on Analysis](#4-valleytalk-for-sve---add-on-analysis)
5. [Prompt Engineering System](#5-prompt-engineering-system)
6. [Data Schemas](#6-data-schemas)
7. [Security Analysis](#7-security-analysis)
8. [Key Findings & Insights](#8-key-findings--insights)

---

## 1. Architecture Overview

```
┌─────────────────────────────────────────────────────────────┐
│                    SMAPI / Stardew Valley                    │
├─────────────────────────────────────────────────────────────┤
│  ValleyTalk.dll (C# SMAPI Mod)                              │
│  ├── LLMClient        → HTTP POST to OpenRouter/DeepSeek    │
│  ├── PromptBuilder    → Assembles prompts from CP data      │
│  ├── DialogueHook     → Intercepts game dialogue events     │
│  ├── TranslationSvc   → Handles i18n & translation mode     │
│  └── ConfigHandler    → Manages config.json                 │
├─────────────────────────────────────────────────────────────┤
│  [CP] ValleyTalk Base (Content Patcher)                     │
│  ├── Prompts.json     → 864+ prompt template keys           │
│  ├── GameSummary.json → World context (locations, festivals)│
│  ├── bio/*.json       → 33 NPC biographies                  │
│  └── i18n/*.json      → English + Chinese translations      │
├─────────────────────────────────────────────────────────────┤
│  ValleyTalk for SVE (Content Patcher Add-on)                │
│  ├── GameSummary.json → 20 SVE villagers                    │
│  ├── Locations.json   → 6 SVE locations                     │
│  └── bio/*.json       → 20 SVE NPC biographies              │
└─────────────────────────────────────────────────────────────┘
```

**Dependencies**:
- SMAPI 4.1.0+ (Stardew Modding API)
- Pathoschild.ContentPatcher
- Newtonsoft.Json
- 0Harmony (runtime patching)
- MonoGame.Framework

---

## 2. ValleyTalk Base - DLL Analysis

### 2.1 Assembly Metadata
- **Assembly**: ValleyTalk, Version 1.0.2.0
- **Architecture**: MSIL (.NET)
- **Entry Point**: `dandm1.ValleyTalk` → `ValleyTalk.dll`
- **Obfuscation**: None detected (standard MSIL)

### 2.2 Key Classes (Inferred from Architecture)

| Class | Purpose |
|-------|---------|
| `ValleyTalkMod` | Main SMAPI entry point, implements `IMod` |
| `LLMClient` | HTTP client for OpenRouter/DeepSeek API |
| `PromptBuilder` | Assembles prompts from Content Patcher data |
| `DialogueHook` | Game event handlers (dialogue triggers) |
| `TranslationService` | Translation mode and i18n resolution |
| `ConfigHandler` | Configuration model and GMCM integration |

### 2.3 LLM API Integration

**Configuration** (from `config.json`):
```json
{
  "Provider": "DeepSeek",
  "ModelName": "deepseek-chat",
  "ServerAddress": "https://openrouter.ai/api",
  "PromptFormat": "[INST] {system}\n{prompt}[/INST]\n{response_start}",
  "QueryTimeout": 30,
  "ApiKey": "sk-[REDACTED：历史泄漏key已吊销]",
  "ApplyTranslation": true
}
```

**API Flow**:
1. Construct system prompt from `ValleyTalk/Prompts` data
2. Build context from `ValleyTalk/GameSummary` + `ValleyTalk/Bios/{Name}`
3. Format using Llama-style instruction template (`[INST]...[/INST]`)
4. POST JSON to OpenRouter-compatible endpoint
5. Parse response via Newtonsoft.Json
6. Inject generated dialogue into game

### 2.4 Game Hooks

**SMAPI Integration Points**:
- **Dialogue Generation**: Intercepts NPC dialogue responses
- **Gift Events**: Triggers when farmer gives/receives gifts
- **Location Awareness**: Tracks conversation location
- **Event Tracking**: Monitors game state changes
- **Schedule Patching**: Modifies NPC schedules via Harmony
- **Typed Dialogue**: `LeftAlt` key initiates free-form conversation

**Frequency Controls**:
- `GeneralFrequency: 2` - Regular dialogue trigger rate
- `MarriageFrequency: 3` - Spouse dialogue rate
- `GiftFrequency: 3` - Gift reaction rate
- `TypedResponses: "Always"` - Always use typed input mode

---

## 3. ValleyTalk Base - Data Architecture

### 3.1 File Structure

```
[CP] ValleyTalk Base/
├── manifest.json              # CP manifest
├── content.json               # Includes all data files
├── assets/
│   ├── Prompts.json           # 864 prompt template keys
│   ├── GameSummary.json       # World context
│   └── bio/                   # 33 NPC biography files
│       ├── Abigail.json, Alex.json, Caroline.json, ...
├── i18n/
    ├── default.json           # English (297 entries)
    └── zh.json                # Chinese (297 entries)
```

### 3.2 Content Patcher Targets

| Target Path | Data Type | Source |
|-------------|-----------|--------|
| `ValleyTalk/Prompts` | Prompt templates | `assets/Prompts.json` |
| `ValleyTalk/GameSummary` | World context | `assets/GameSummary.json` |
| `ValleyTalk/Bios/{NPCName}` | NPC data | `assets/bio/{NPCName}.json` |

### 3.3 Prompt Template System (`ValleyTalk/Prompts`)

**864+ template keys** organized into categories:

| Category | Count | Examples |
|----------|-------|----------|
| System/Instructions | ~10 | `systemPrompt`, `instructionsHeading` |
| Core Context | ~10 | `coreFarmerGender`, `coreMarried` |
| Relationships | ~30 | `specialRelationshipDating`, `spousePoly` |
| Friendship Levels | ~10 | `nonSpouseFriendshipFriends` |
| Spouse Actions | ~10 | `spouseActionFunLeave`, `spouseActionJobReturn` |
| Gift Reactions | ~10 | `giftLoved`, `giftHate` |
| Locations | ~30 | `locationAtHome`, `locationBeach` |
| Special Dates | ~20 | `specialDatesSpring1`, `specialDatesBirthday` |
| Recent Events | ~20 | `recentEventsBoulder`, `recentEventsMarried` |
| Time/Weather | ~20 | `dateTimeDayOfSeason`, `weatherRain` |
| Output Format | ~10 | `instructionsSingleLine`, `responseStart` |
| UI Strings | ~10 | `uiThinking`, `uiYourResponse` |
| Config/GMCM | ~20 | `configEnable`, `configProvider` |
| Gender Variants | ~600+ | Each key × `.FemaleNpc` + `.MaleNpc` |

### 3.4 Game Summary (`ValleyTalk/GameSummary`)

**Section Order** (configurable):
```json
"SectionOrder": {
  "Intro": false,           // Game premise
  "FarmerBackground": false,// Player backstory
  "Seasons": true,          // 4 seasons with crops/forage
  "Locations": true,        // 17 locations
  "Festivals": true,        // 8 festivals
  "Villagers": true,        // 32 NPCs
  "Outro": false            // Static world note
}
```

**Locations** (17):
- The Farm, Pierre's, JojaMart, Saloon, Clinic, Blacksmith
- Library/Museum, Community Center, Cindersap Forest
- Marnie's Ranch, Wizard's Tower, Mountain, Adventurer's Guild
- The Mines, Spa, Train Station, Quarry, Beach/Fish Shop
- Ginger Island, Tide Pools, Desert, Oasis, Skull Cavern

**Festivals** (8):
- Egg Festival (Spring 13), Flower Dance (Spring 24)
- Luau (Summer 11), Dance of Moonlight Jellies (Summer 28)
- Stardew Valley Fair (Fall 16), Spirit's Eve (Fall 27)
- Festival of Ice (Winter 8), Feast of Winter Star (Winter 25)

### 3.5 NPC Biography Schema

```typescript
interface NPCBio {
  Biography: string;              // Multi-paragraph backstory
  Relationships: {
    [npcName: string]: {
      id: string;
      Heading: string;
      Description: string;
    }
  };
  Traits: {
    [traitName: string]: {
      id: string;
      Heading: string;
      Description: string;
    }
  };
  BiographyEnd: string;           // Closing summary
  ExtraPortraits: { [id: string]: string };  // Portrait mappings
  Unique: string;                 // Unique portrait description
  Preoccupations: string[];       // Conversation topics
  Dialogue: { [day: string]: string };  // Sample dialogue
  HomeLocationBed: boolean;       // Has bedroom
  PromptOverrides?: { [key: string]: string };  // NPC-specific prompts
}
```

### 3.6 i18n & Localization

**Dual-variant system** - Every prompt has 3 versions:
- Base: `systemPrompt`
- Female NPC: `systemPrompt.FemaleNpc`
- Male NPC: `systemPrompt.MaleNpc`

**Gender conditional syntax**:
```
"coreFarmerGender": "The farmer is ${male^female}$. ${he^she}$ is..."
```
- Before `^`: male option
- After `^`: female option

**Variable interpolation**:
- `{{Name}}` - NPC name
- `{{i18n:key}}` - i18n lookup
- `{{days}}`, `{{giftName}}` - Runtime values
- `@` - Farmer name (in output)
- `$h`, `$0`, `$s` - Emotion tokens
- `#$b#`, `#$e#` - Screen break markers
- `% ` - Response option prefix

---

## 4. ValleyTalk for SVE - Add-on Analysis

### 4.1 Architecture

**Pure Content Patcher** - No DLL. Uses `Include` actions only.

```
ValleyTalk for SVE/
├── manifest.json          # CP manifest (v0.1.0)
├── content.json           # Includes-only (Format 2.3.0)
├── assets/
│   ├── GameSummary.json   # 20 SVE villagers
│   ├── Locations.json     # 6 SVE locations
│   └── bio/               # 23 files (20 new + 3 patches)
│       ├── Sophia.json, Victor.json, Olivia.json, ...
```

### 4.2 SVE Villagers Added (20)

| ID | Name | Description |
|----|------|-------------|
| Alesia | Alesia | Adventurer's Guild archer |
| Andy | Andy | Gruff elderly farmer |
| Apples | Apples | Energetic Junimo |
| Camilla | Camilla | Mysterious witch |
| Claire | Claire | Shy Joja Mart worker |
| Gunther | Gunther | Museum curator |
| Hank | Hank | Grampleton handyman |
| Henchman | Henchman | Marshlands goblin |
| Isaac | Isaac | Abrasive warrior |
| Jadu | Jadu | Desert sentry |
| Jolyne | Jolyne | Guild leader |
| Lance | Lance | Galdora researcher |
| Marlon | Marlon | Guild leader |
| Martin | Martin | Shy Joja worker |
| Morgan | Morgan | Non-binary magic apprentice |
| Morris | Morris | Joja manager |
| Olivia | Olivia | Wealthy widow |
| Peaches | Peaches | Junimo friend |
| Scarlett | Scarlett | Grampleton farmhand |
| Sophia | Sophia | Vineyard owner |
| Susan | Susan | Elderly farmer |
| Treyvon | Treyvon | Wholesale store owner |
| Victor | Victor | Engineering graduate |

### 4.3 SVE Locations Added (6)

| ID | Region | Name |
|----|--------|------|
| BlueMoonVineyard | Cindersap Forest | Blue Moon Vineyard |
| CindersapForest | Cindersap Forest | Cindersap Forest (updated) |
| FairhavenFarm | Cindersap Forest | Fairhaven Farm |
| AuroraVineyard | West Cindersap Forest | Aurora Vineyard |
| Grampleton | Grampleton | Grampleton |
| Highlands | Highlands | Highlands |

### 4.4 SVE Bio Schema Extensions

**New fields not in base game**:

| Field | Description | Example |
|-------|-------------|---------|
| `ExtraPortraits` | Portrait mappings | `{"7": "disgusted", "6": "crying"}` |
| `Unique` | Portrait description | `""` (empty for most) |
| `Preoccupations` | Topic tags | `["anime", "cosplay", "Grampleton"]` |
| `Dialogue` | Sample lines | `{}` (empty for most) |
| `HomeLocationBed` | Bedroom flag | `true`/`false` |
| `UsePatchedDialogue` | Patched dialogue flag | Always `true` |

### 4.5 Relationship Patching

SVE NPCs patch **14 base game NPCs** to add SVE relationships:

| SVE NPC | Patches Base NPC | Relationship |
|---------|-----------------|--------------|
| Sophia | Emily, Haley | Close friends |
| Olivia | Caroline, Jodi, Pam, Leah | Friendships |
| Claire | Shane | Best friends |
| Andy | Lewis, Pierre | Mutual respect |
| Alesia | Marlon | Guild member |
| Gunther | Penny, Willy, Robin | Professional |
| Marlon | Wizard, Marnie, Krobus | Various |
| Morris | Lewis, Pierre, Shane | Business/political |
| Susan | Lewis, Marnie | Town relationships |

**Patching technique**:
```json
{
  "Action": "EditData",
  "Target": "ValleyTalk/Bios/Emily",
  "TargetField": ["Relationships"],
  "Priority": "Late",
  "Entries": {
    "Sophia": {
      "id": "Sophia",
      "Heading": "Sophia",
      "Description": "Emily is close friends with Sophia..."
    }
  }
}
```

Uses `Priority: "Late"` to ensure SVE patches apply after other mods.

### 4.6 Base vs SVE Comparison

| Aspect | Base Mod | SVE Add-on |
|--------|----------|------------|
| Version | 1.0.2 | 0.1.0 |
| Format | 2.0+ | 2.3.0 |
| Has DLL | Yes | No |
| NPCs | 33 | +20 |
| Locations | 17 | +6 |
| Action types | EditData + Include | Include only |
| Bio fields | 7 required | 10 (adds ExtraPortraits, Unique, Preoccupations, Dialogue, HomeLocationBed, UsePatchedDialogue) |
| i18n | English + Chinese | Inherits from base |

---

## 5. Prompt Engineering System

### 5.1 Prompt Construction Pipeline

```
┌──────────────────────────────────────────────────────────┐
│ 1. System Prompt                                          │
│    "You are an expert computer game writer..."            │
├──────────────────────────────────────────────────────────┤
│ 2. Game Context                                           │
│    "You are creating dialogue for Stardew Valley..."      │
├──────────────────────────────────────────────────────────┤
│ 3. Game Summary                                           │
│    Seasons, Locations, Festivals, Villagers               │
├──────────────────────────────────────────────────────────┤
│ 4. NPC Biography                                          │
│    Background, Relationships, Traits                      │
├──────────────────────────────────────────────────────────┤
│ 5. Core Context                                           │
│    Farmer gender, relationship status, location, time     │
├──────────────────────────────────────────────────────────┤
│ 6. Other NPCs                                             │
│    Who else is present                                    │
├──────────────────────────────────────────────────────────┤
│ 7. Conversation History                                   │
│    Previous dialogue lines                                │
├──────────────────────────────────────────────────────────┤
│ 8. Instructions                                           │
│    Output format, emotion tags, break rules               │
├──────────────────────────────────────────────────────────┤
│ 9. Response Start                                         │
│    "- " (single line, emotion tag)                        │
└──────────────────────────────────────────────────────────┘
```

### 5.2 Output Format Specification

From `instructionsSingleLine`:
- Single line preceded with `- `
- Properly punctuated and capitalized
- Use `@` for farmer name
- Emotion tokens: `$h` (happy), `$0` (neutral), `$s` (sad)
- Breaks: `#$b#` (standard) or `#$e#` (significant) - max 24 words between
- No emojis, asterisks, or special characters

From `instructionsResponses`:
- 2-4 options per response
- Format: `% ` prefix + text (max 12 words)
- In voice of farmer, from farmer perspective

### 5.3 Context-Aware Features

**Relationship States**:
- Roommates (non-romantic): `coreRoommates`
- Married: `coreMarried`, `coreMarriedSince`
- Polyamorous: `spousePolyView`
- Dating: `specialRelationshipDating` (public/discrete)
- Engaged: `specialRelationshipEngaged` (with countdown)
- Divorced: `specialRelationshipDivorced`
- Rejected proposal: `specialRelationshipProposalRejected`

**Friendship Levels**:
- First conversation (strangers)
- Strangers (spoken before)
- Acquaintances
- Friends
- Close friends
- Want to date
- Intimate
- Non-single adult (8+ hearts)
- Child (8+ hearts, idolizes farmer)

**Dynamic Context**:
- Location (home, town, beach, saloon, etc.)
- Time of day (early morning, midday, evening)
- Weather (rain, snow, lightning, green rain)
- Recent events (CC completion, marriage, babies)
- Farm status (crops, animals, buildings, wealth)
- Special dates (festivals, birthdays)
- Gift reactions (loved, liked, neutral, hated)

---

## 6. Data Schemas

### 6.1 Prompts.json Schema

```typescript
interface PromptsData {
  Changes: [{
    Action: "EditData";
    Target: "ValleyTalk/Prompts";
    Priority: "Early";
    Entries: {
      [key: string]: string;  // Key → {{i18n:key}} reference
    }
  }];
}
```

### 6.2 GameSummary.json Schema

```typescript
interface GameSummary {
  Changes: [{
    Action: "EditData";
    Target: "ValleyTalk/GameSummary";
    Priority: "Early";
    Entries: {
      SectionOrder: {
        [section: string]: boolean;  // Include/exclude flags
      };
      Intro: { Text: string; Entries: {} };
      FarmerBackground: { Text: string; Entries: {} };
      Seasons: {
        Text: string;
        Entries: {
          [season: string]: {
            id: string;
            Name: string;
            Description: string;
            Crops?: string[];
            Forage: string[];
          }
        }
      };
      Locations: {
        Text: string;
        Entries: {
          [location: string]: {
            id: string;
            Region: string;
            Name: string;
            Description: string;
          }
        }
      };
      Festivals: {
        Text: string;
        Entries: {
          [festival: string]: {
            id: string;
            Name: string;
            Description: string;
          }
        }
      };
      Villagers: {
        Text: string;
        Entries: {
          [npc: string]: {
            id: string;
            Name: string;
            Description: string;
          }
        }
      };
      Outro: { Text: string; Entries: {} };
    }
  }];
}
```

### 6.3 NPC Bio Schema

```typescript
interface NPCBio {
  Biography: string;
  Relationships: {
    [npcName: string]: {
      id: string;
      Heading: string;
      Description: string;
    }
  };
  Traits: {
    [traitName: string]: {
      id: string;
      Heading: string;
      Description: string;
    }
  };
  BiographyEnd: string;
  ExtraPortraits?: { [id: string]: string };
  Unique?: string;
  Preoccupations?: string[];
  Dialogue?: { [day: string]: string };
  HomeLocationBed?: boolean;
  UsePatchedDialogue?: boolean;
  PromptOverrides?: { [key: string]: string };
}
```

### 6.4 i18n Schema

```typescript
interface I18nData {
  [key: string]: string;  // Key → localized string
  // Gender conditionals: "${male^female}$"
  // Variables: "{{Name}}", "{{days}}", etc.
}
```

---

## 7. Security Analysis

### 7.1 Findings

| Issue | Severity | Description |
|-------|----------|-------------|
| **Exposed API Key** | 🔴 Critical | `config.json` contains plaintext OpenRouter API key |
| **No Obfuscation** | 🟡 Low | DLL is standard MSIL, easily decompiled |
| **No Anti-Debug** | 🟡 Low | No runtime protection detected |

### 7.2 API Key Exposure

**File**: `ValleyTalk/ValleyTalk/config.json`
```json
"ApiKey": "sk-[REDACTED：历史泄漏key已吊销]"
```

**Risk**: This is a valid OpenRouter API key embedded in plaintext. Anyone with access to this mod file can:
- Use the key for their own API calls
- Exhaust the key's quota/credits
- Access the associated OpenRouter account

**Recommendation**: API keys should be stored securely or input by users at runtime.

---

## 8. Key Findings & Insights

### 8.1 Design Strengths

1. **Sophisticated Prompt Engineering**: 864+ prompt templates with gender variants create highly contextual dialogue
2. **Rich Context Awareness**: Tracks relationships, location, time, weather, events, farm status
3. **Modular Architecture**: Clean separation between DLL logic and Content Patcher data
4. **Extensible**: SVE add-on demonstrates easy extension pattern
5. **i18n Support**: Full localization with gender-aware variants
6. **Relationship Web**: NPCs know about each other, creating realistic social dynamics

### 8.2 Technical Innovations

1. **Gender-Conditional Syntax**: `${male^female}$` allows single-template gender adaptation
2. **Dual-Variant i18n**: Every prompt has MaleNpc/FemaleNpc variants
3. **Priority-Based Patching**: `Priority: "Late"` ensures cross-mod compatibility
4. **Deep Merge**: `TargetField` merges specific sections without overwriting
5. **Prompt Overrides**: NPCs can override specific prompt templates

### 8.3 Notable NPC Designs

- **Shane**: Has `PromptOverrides` for depressed/unfriendly dialogue at low friendship
- **Krobus**: Non-human (shadow creature), `HomeLocationBed: false`
- **Sophia** (SVE): Richest `ExtraPortraits` (4 entries: disgusted, crying, laughing, shocked)
- **Morgan** (SVE): Non-binary character with they/them pronouns
- **Apples/Peaches** (SVE): Junimo magical creatures

### 8.4 Content Patcher Mastery

The mod demonstrates advanced Content Patcher techniques:
- **Include actions** for modular data
- **TargetField** for surgical edits
- **Priority** for load-order control
- **i18n tokens** for localization
- **Data targeting** (`ValleyTalk/Prompts`, `ValleyTalk/Bios/*`)

### 8.5 Potential Improvements

1. **Secure API key storage** (critical)
2. **More i18n languages** (only EN/ZH currently)
3. **SVE version is early** (v0.1.0, fewer features than base)
4. **Dialogue history persistence** could be expanded
5. **Performance optimization** for frequent LLM calls

---

## Appendix A: Complete File Inventory

### Base Mod (ValleyTalk)
- `ValleyTalk.dll` - Core mod (282 KB)
- `config.json` - Runtime configuration
- `manifest.json` - SMAPI manifest

### Content Pack ([CP] ValleyTalk Base)
- `content.json` - Data file includes
- `manifest.json` - CP manifest
- `assets/Prompts.json` - 864 prompt keys
- `assets/GameSummary.json` - World context
- `assets/bio/*.json` - 33 NPC bios
- `i18n/default.json` - English strings
- `i18n/zh.json` - Chinese strings

### SVE Add-on (ValleyTalk for SVE)
- `content.json` - Include directives
- `manifest.json` - CP manifest
- `assets/GameSummary.json` - SVE villagers
- `assets/Locations.json` - SVE locations
- `assets/bio/*.json` - 23 bio files (20 new + 3 patches)

---

*Report generated from reverse engineering analysis*
*Mods by dandm1 | ValleyTalk v1.0.2 | ValleyTalk for SVE v0.1.0*
