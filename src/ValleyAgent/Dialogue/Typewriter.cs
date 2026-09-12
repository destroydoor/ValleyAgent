using System;
using Microsoft.Xna.Framework;

namespace ValleyAgent.Dialogue;

/// <summary>
///     Typewriter effect for streaming text display at 30 chars/sec.
/// </summary>
public sealed class Typewriter
{
    private const float CharsPerSecond = 30f;
    private readonly string _fullText;
    private float _charAccumulator;
    private int _visibleCount;

    public Typewriter(string fullText)
    {
        _fullText = fullText ?? string.Empty;
        _charAccumulator = 0f;
        _visibleCount = 0;
    }

    public string CurrentVisible
    {
        get => _fullText.Substring(0, Math.Min(_visibleCount, _fullText.Length));
    }

    public bool IsComplete
    {
        get => _visibleCount >= _fullText.Length;
    }

    public event Action? OnComplete;

    public void Update(GameTime gameTime)
    {
        if (IsComplete)
        {
            return;
        }

        _charAccumulator += (float)(gameTime.ElapsedGameTime.TotalSeconds * CharsPerSecond);
        while (_charAccumulator >= 1f && !IsComplete)
        {
            _charAccumulator -= 1f;
            _visibleCount++;
        }

        if (IsComplete)
        {
            OnComplete?.Invoke();
        }
    }

    public void Skip()
    {
        _visibleCount = _fullText.Length;
        OnComplete?.Invoke();
    }
}