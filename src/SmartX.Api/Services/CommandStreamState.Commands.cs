using SmartX.Shared.Commands;

namespace SmartX.Api.Services;

// Part 2 — operator commands: FIFO command queue, undo (Rubric: Stack<T>) and history (Rubric: SortedDictionary).
public sealed partial class CommandStreamState
{
    /// <summary>FIFO of operator commands awaiting execution.</summary>
    private readonly Queue<CommandEnvelope> _commandQueue = new();

    /// <summary>History of operator actions for Undo. One entry per action, so a bulk "shut down all valves" reverts in one step.</summary>
    private readonly Stack<CommandBatch> _undoStack = new();

    /// <summary>Executed commands ordered by timestamp, feeding the history page.</summary>
    private readonly SortedDictionary<StreamKey, CommandRecord> _commandHistory = new();

    public const int MaxHistory = 1000;

    public int CommandQueueDepth { get { lock (_lock) return _commandQueue.Count; } }
    public int UndoDepth { get { lock (_lock) return _undoStack.Count; } }
    public int HistoryCount { get { lock (_lock) return _commandHistory.Count; } }

    public string? NextUndoLabel
    {
        get { lock (_lock) return _undoStack.TryPeek(out var top) ? top.Label : null; }
    }

    public CommandEnvelope EnqueueCommand(CommandEnvelope envelope)
    {
        lock (_lock)
        {
            if (envelope.Id == Guid.Empty) envelope.Id = Guid.NewGuid();
            if (envelope.QueuedAt == default) envelope.QueuedAt = DateTimeOffset.UtcNow;
            envelope.TargetMacs = envelope.TargetMacs.Select(NormaliseMac).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _commandQueue.Enqueue(envelope);
            return envelope;
        }
    }

    public CommandEnvelope? DequeueCommand()
    {
        lock (_lock) return _commandQueue.TryDequeue(out var e) ? e : null;
    }

    /// <summary>
    /// Executes one queued operator action against every target device, records each change in the
    /// sorted history and pushes the action onto the undo stack as a single entry.
    /// </summary>
    public CommandBatch ProcessCommand(CommandEnvelope envelope)
    {
        lock (_lock)
        {
            var now = DateTimeOffset.UtcNow;
            var batch = new CommandBatch
            {
                Id = envelope.Id,
                Label = envelope.Label,
                Command = envelope.Command,
                Timestamp = now,
                IssuedBy = envelope.IssuedBy
            };

            foreach (var mac in envelope.TargetMacs)
            {
                if (!_devices.TryGetValue(mac, out var device)) continue;

                var record = new CommandRecord
                {
                    Id = Guid.NewGuid(),
                    BatchId = batch.Id,
                    MacAddress = device.MacAddress,
                    DeviceName = device.Name,
                    Command = envelope.Command,
                    Priority = envelope.Priority,
                    Reason = envelope.Reason,
                    Timestamp = now,
                    PreviousState = device.CurrentState,
                    NewState = StateAfter(envelope.Command),
                    IssuedBy = envelope.IssuedBy
                };

                device.CurrentState = record.NewState;
                device.LastCommandAt = now;
                RecordUnsafe(record);
                batch.Records.Add(record);

                // Only an explicit Reset clears a fault. E-STOP must leave the device faulted.
                if (envelope.Command == CommandKind.Reset) ClearFaultUnsafe(device.MacAddress);
            }

            if (batch.Records.Count == 0)
                throw new KeyNotFoundException($"None of the target devices for '{envelope.Label}' are registered.");

            _undoStack.Push(batch);
            return batch;
        }
    }

    /// <summary>
    /// Pops the most recent operator action and restores every device it touched to the state it
    /// was in beforehand. Returns the reversing batch, or null when there is nothing to undo.
    /// </summary>
    public CommandBatch? Undo(string operatorName = "operator")
    {
        lock (_lock)
        {
            if (!_undoStack.TryPop(out var last)) return null;

            var now = DateTimeOffset.UtcNow;
            var reverse = new CommandBatch
            {
                Id = Guid.NewGuid(),
                Label = $"Undo: {last.Label}",
                Command = Inverse(last.Command),
                Timestamp = now,
                IssuedBy = operatorName
            };

            // Walk the batch backwards so a device touched twice ends in its original state.
            for (var i = last.Records.Count - 1; i >= 0; i--)
            {
                var original = last.Records[i];
                original.Undone = true;          // same instance as in the history, so the history shows it
                if (!_devices.TryGetValue(original.MacAddress, out var device)) continue;

                var record = new CommandRecord
                {
                    Id = Guid.NewGuid(),
                    BatchId = reverse.Id,
                    MacAddress = device.MacAddress,
                    DeviceName = device.Name,
                    Command = CommandFor(original.PreviousState),
                    Priority = original.Priority,
                    Reason = $"Undo of {original.Command}",
                    Timestamp = now,
                    PreviousState = device.CurrentState,
                    NewState = original.PreviousState,
                    IssuedBy = operatorName
                };
                device.CurrentState = original.PreviousState;
                device.LastCommandAt = now;
                RecordUnsafe(record);
                reverse.Records.Add(record);
            }

            // Label the undo batch with the restoring command when every device went back the same way.
            var restoring = reverse.Records.Select(r => r.Command).Distinct().ToList();
            if (restoring.Count == 1) reverse.Command = restoring[0];
            return reverse;
        }
    }

    private void RecordUnsafe(CommandRecord record)
    {
        _commandHistory[new StreamKey(record.Timestamp, ++_sequence)] = record;
        while (_commandHistory.Count > MaxHistory) _commandHistory.Remove(_commandHistory.Keys.First());
    }

    /// <summary>Newest records first, optionally for one device.</summary>
    public List<CommandRecord> History(string? mac = null, int take = 50)
    {
        var normalised = string.IsNullOrWhiteSpace(mac) ? null : NormaliseMac(mac);
        lock (_lock)
        {
            return _commandHistory.Values
                .Where(r => normalised is null || r.MacAddress.Equals(normalised, StringComparison.OrdinalIgnoreCase))
                .Reverse()
                .Take(Math.Clamp(take, 1, MaxHistory))
                .Select(x => Clone(x))
                .ToList();
        }
    }

    public static CommandKind Inverse(CommandKind command) => command switch
    {
        CommandKind.TurnOn => CommandKind.TurnOff,
        CommandKind.TurnOff => CommandKind.TurnOn,
        _ => CommandKind.Reset
    };

    /// <summary>The command that puts a device back into <paramref name="state"/> (used by Undo).</summary>
    public static CommandKind CommandFor(string state) => state switch
    {
        "ON" => CommandKind.TurnOn,
        "OFF" => CommandKind.TurnOff,
        "RUNNING" => CommandKind.Trigger,
        "STOPPED" => CommandKind.EmergencyStop,
        _ => CommandKind.Reset
    };

    private static string StateAfter(CommandKind command) => command switch
    {
        CommandKind.TurnOn => "ON",
        CommandKind.TurnOff => "OFF",
        CommandKind.Trigger => "RUNNING",
        CommandKind.Reset => "IDLE",
        CommandKind.EmergencyStop => "STOPPED",
        _ => "UNKNOWN"
    };

    private static CommandRecord Clone(CommandRecord r) => new()
    {
        Id = r.Id, BatchId = r.BatchId, MacAddress = r.MacAddress, DeviceName = r.DeviceName, Command = r.Command,
        Priority = r.Priority, Reason = r.Reason, Timestamp = r.Timestamp, PreviousState = r.PreviousState,
        NewState = r.NewState, IssuedBy = r.IssuedBy, Undone = r.Undone
    };
}
