using Microsoft.Extensions.Logging;

namespace CodexTrayApp.Core.Pricing;

/// <summary>Holds the pricing table in force and reloads it when the override path changes.</summary>
public sealed class PricingProvider
{
    private readonly ILogger<PricingProvider> _logger;
    private readonly object _sync = new();
    private PricingTable _current = PricingTable.Empty;
    private string? _overridePath;
    private bool _loaded;

    public PricingProvider(string bundledPath, ILogger<PricingProvider> logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledPath);
        BundledPath = bundledPath;
        _logger = logger;
    }

    /// <summary>Raised after a reload that produced a different table. May fire on any thread.</summary>
    public event EventHandler? Changed;

    public string BundledPath { get; }

    public PricingTable Current
    {
        get
        {
            lock (_sync)
            {
                if (!_loaded)
                {
                    _current = PricingLoader.Load(_overridePath, BundledPath, _logger);
                    _loaded = true;
                }

                return _current;
            }
        }
    }

    /// <summary>The override in force, or null for the bundled file.</summary>
    public string? OverridePath
    {
        get
        {
            lock (_sync)
            {
                return _overridePath;
            }
        }
    }

    /// <summary>True when an override was asked for but the table in force did not come from it.</summary>
    public bool OverrideFailed
    {
        get
        {
            var table = Current;
            var overridePath = OverridePath;
            return overridePath is not null && !string.Equals(table.SourcePath, overridePath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Loads from <paramref name="overridePath"/> (or the bundled file when null). Returns true when the table changed.</summary>
    public bool Reload(string? overridePath)
    {
        overridePath = string.IsNullOrWhiteSpace(overridePath) ? null : overridePath.Trim();
        PricingTable previous;
        PricingTable next;
        lock (_sync)
        {
            previous = _loaded ? _current : PricingTable.Empty;
            _overridePath = overridePath;
            next = PricingLoader.Load(overridePath, BundledPath, _logger);
            _current = next;
            _loaded = true;
        }

        var changed = previous.SourcePath != next.SourcePath
            || previous.Models.Count != next.Models.Count
            || previous.EffectiveDate != next.EffectiveDate;
        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }

        return changed;
    }
}
