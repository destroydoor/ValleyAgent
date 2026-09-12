# ValleyTalk AI Agent Upgrade - Issues

## Known Issues
- Original ValleyTalk.dll has no source code → must reverse-engineer behavior
- config.json contains exposed API key (sk-[REDACTED]) → must NOT commit
- NPC Adventures targets .NET 4.5.2 → cannot reference DLLs
- SMAPI testing ecosystem is immature → integration tests must be manual

## Compilation Fixes Applied (Wave 1)

### Issues Found and Fixed:

1. **ModConfig.cs**: Removed stray `+` symbol on line 2
2. **ModEntry.cs**: Removed duplicate methods/classes that were appended incorrectly
3. **KimiProvider.cs**:
   - Fixed `JsonNamingPolicy.SnakeCaseLower` (.NET 8 only) → set to `null` since all DTOs have `[JsonPropertyName]`
   - Fixed CS1626: Moved `yield return` out of try-catch by extracting stream reading to `ReadStreamDeltasAsync()` helper method
   - Moved `[EnumeratorCancellation]` attribute from method to parameter
4. **OpenAICompatibleProvider.cs**:
   - Fixed duplicate `deltasBuffer` variable in ChatCompletionAsync
   - Fixed broken StreamCompletionAsync method structure
   - Moved `[EnumeratorCancellation]` to parameter
5. **NpcData.cs**: Fixed `JsonStringEnumConverter<GiftTaste>` (.NET 7+ only) → `JsonStringEnumConverter` (.NET 6 compatible)

### Build Status:
- Cannot fully build due to missing Stardew Valley game folder (expected in dev environment)
- Syntax errors in core files have been resolved
- Remaining build dependency on SMAPI ModBuildConfig requiring game installation

### Key Learnings:
- .NET 6 limitations: No `JsonNamingPolicy.SnakeCaseLower`, no generic `JsonStringEnumConverter<T>`
- C# async iterators: Cannot use `yield return` inside try-catch blocks (CS1626)
- `[EnumeratorCancellation]` must be placed on the CancellationToken parameter, not the method

## Open Questions
- None currently (all resolved via Metis review)