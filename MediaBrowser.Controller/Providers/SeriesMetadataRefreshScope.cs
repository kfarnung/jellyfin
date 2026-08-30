using System;
using System.Threading;

namespace MediaBrowser.Controller.Providers;

/// <summary>
/// Tracks when a series refresh is only priming series-level metadata ahead of child refresh.
/// </summary>
public static class SeriesMetadataRefreshScope
{
    private static readonly AsyncLocal<int> _childReconciliationSuppressionDepth = new();

    /// <summary>
    /// Gets a value indicating whether series child reconciliation should be skipped for the current async flow.
    /// </summary>
    public static bool IsChildReconciliationSuppressed => _childReconciliationSuppressionDepth.Value > 0;

    /// <summary>
    /// Suppresses series season and episode reconciliation until the returned scope is disposed.
    /// </summary>
    /// <returns>A disposable scope.</returns>
    public static IDisposable SuppressChildReconciliation()
    {
        _childReconciliationSuppressionDepth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        public void Dispose()
        {
            if (_childReconciliationSuppressionDepth.Value > 0)
            {
                _childReconciliationSuppressionDepth.Value--;
            }
        }
    }
}
