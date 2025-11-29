using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using TidyWindow.Core.Maintenance;

namespace TidyWindow.App.Services;

public sealed class EssentialsQueueStateStore : IEssentialsQueueStateStore
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly object _syncRoot = new();

    public EssentialsQueueStateStore()
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TidyWindow");
        Directory.CreateDirectory(root);
        _filePath = Path.Combine(root, "essentials-queue.json");
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
    }

    public IReadOnlyList<EssentialsQueueOperationRecord> Load()
    {
        lock (_syncRoot)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return Array.Empty<EssentialsQueueOperationRecord>();
                }

                using var stream = File.OpenRead(_filePath);
                var payload = JsonSerializer.Deserialize<StatePayload>(stream, _serializerOptions);
                return payload?.Operations ?? new List<EssentialsQueueOperationRecord>();
            }
            catch
            {
                return Array.Empty<EssentialsQueueOperationRecord>();
            }
        }
    }

    public void Save(IReadOnlyList<EssentialsQueueOperationRecord> operations)
    {
        if (operations is null)
        {
            throw new ArgumentNullException(nameof(operations));
        }

        lock (_syncRoot)
        {
            try
            {
                var payload = new StatePayload { Operations = operations.ToList() };
                var tempPath = _filePath + ".tmp";

                using (var stream = File.Create(tempPath))
                {
                    JsonSerializer.Serialize(stream, payload, _serializerOptions);
                    stream.Flush(true);
                }

                File.Copy(tempPath, _filePath, overwrite: true);
                File.Delete(tempPath);
            }
            catch
            {
                // Persistence failures shouldn't block the queue; swallow and keep running.
            }
        }
    }

    private sealed class StatePayload
    {
        public List<EssentialsQueueOperationRecord> Operations { get; set; } = new();
    }
}
