using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Streams registry tweak state evaluations by orchestrating <see cref="IRegistryStateService"/> probes with controlled concurrency.
/// </summary>
public sealed class RegistryStateWatcher
{
    private readonly IRegistryStateService _registryStateService;
    private readonly int _maxConcurrency;

    public RegistryStateWatcher(IRegistryStateService registryStateService, int maxConcurrency = 4)
    {
        _registryStateService = registryStateService ?? throw new ArgumentNullException(nameof(registryStateService));
        if (maxConcurrency <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxConcurrency), maxConcurrency, "Concurrency must be greater than zero.");
        }

        _maxConcurrency = maxConcurrency;
    }

    /// <summary>
    /// Starts watching a single registry tweak and emits exactly one update when the probe completes.
    /// </summary>
    public IAsyncEnumerable<RegistryStateUpdate> WatchAsync(string tweakId, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            throw new ArgumentException("Tweak identifier must be provided.", nameof(tweakId));
        }

        return WatchInternalAsync(new[] { tweakId }, forceRefresh, cancellationToken);
    }

    /// <summary>
    /// Starts watching the supplied registry tweaks and yields updates as soon as individual probes complete.
    /// </summary>
    public IAsyncEnumerable<RegistryStateUpdate> WatchAsync(IEnumerable<string> tweakIds, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        if (tweakIds is null)
        {
            throw new ArgumentNullException(nameof(tweakIds));
        }

        return WatchInternalAsync(tweakIds, forceRefresh, cancellationToken);
    }

    private async IAsyncEnumerable<RegistryStateUpdate> WatchInternalAsync(
        IEnumerable<string> tweakIds,
        bool forceRefresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var normalized = NormalizeTweakIds(tweakIds);
        if (normalized.Length == 0)
        {
            yield break;
        }

        var channel = Channel.CreateUnbounded<RegistryStateUpdate>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        using var throttle = new SemaphoreSlim(_maxConcurrency);
        using var cancellationRegistration = cancellationToken.Register(() =>
        {
            channel.Writer.TryComplete(new OperationCanceledException("Registry state watch cancelled.", cancellationToken));
        });

        var remaining = normalized.Length;

        foreach (var tweakId in normalized)
        {
            _ = Task.Run(async () =>
            {
                var entered = false;

                try
                {
                    await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
                    entered = true;

                    var state = await _registryStateService
                        .GetStateAsync(tweakId, forceRefresh, cancellationToken)
                        .ConfigureAwait(false);

                    var update = RegistryStateUpdate.Success(tweakId, state);

                    await TryWriteAsync(channel.Writer, update, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Cancellation handled via the registration.
                }
                catch (Exception ex)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    var failure = RegistryStateUpdate.Failure(tweakId, ex);
                    await TryWriteAsync(channel.Writer, failure, CancellationToken.None).ConfigureAwait(false);
                }
                finally
                {
                    if (entered)
                    {
                        throttle.Release();
                    }

                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        channel.Writer.TryComplete();
                    }
                }
            }, CancellationToken.None);
        }

        while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (channel.Reader.TryRead(out var update))
            {
                yield return update;
            }
        }
    }

    private static async Task TryWriteAsync(ChannelWriter<RegistryStateUpdate> writer, RegistryStateUpdate update, CancellationToken cancellationToken)
    {
        try
        {
            await writer.WriteAsync(update, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Reader cancelled; nothing to surface.
        }
        catch (ChannelClosedException)
        {
            // Reader completed before this update could be published.
        }
    }

    private static string[] NormalizeTweakIds(IEnumerable<string> tweakIds)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var tweakId in tweakIds)
        {
            if (string.IsNullOrWhiteSpace(tweakId))
            {
                continue;
            }

            var trimmed = tweakId.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            if (seen.Add(trimmed))
            {
                order.Add(trimmed);
            }
        }

        return order.Count == 0 ? Array.Empty<string>() : order.ToArray();
    }
}

public sealed record RegistryStateUpdate(
    string TweakId,
    RegistryTweakState? State,
    bool IsSuccess,
    string? ErrorMessage,
    Exception? Exception)
{
    public static RegistryStateUpdate Success(string tweakId, RegistryTweakState state)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            throw new ArgumentException("Tweak identifier must be provided.", nameof(tweakId));
        }

        if (state is null)
        {
            throw new ArgumentNullException(nameof(state));
        }

        return new RegistryStateUpdate(tweakId, state, true, null, null);
    }

    public static RegistryStateUpdate Failure(string tweakId, Exception exception)
    {
        if (string.IsNullOrWhiteSpace(tweakId))
        {
            throw new ArgumentException("Tweak identifier must be provided.", nameof(tweakId));
        }

        if (exception is null)
        {
            throw new ArgumentNullException(nameof(exception));
        }

        var message = exception.Message;
        if (string.IsNullOrWhiteSpace(message))
        {
            message = "Registry state probe failed.";
        }

        return new RegistryStateUpdate(tweakId, null, false, message, exception);
    }
}
