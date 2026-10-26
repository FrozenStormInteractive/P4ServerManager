using System.Globalization;

using P4ServerManager.Contracts;

namespace P4ServerManager.Cli.Operations;

/// <summary>
/// Writes the progress of an operation from its successive snapshots: a line each time a step changes status.
/// </summary>
/// <param name="writer">Where the lines go.</param>
/// <param name="rewriteInPlace">
/// True on a terminal: the line of the running step is rewritten with its percentage, then with its result.
/// </param>
internal sealed class OperationProgressWriter(TextWriter writer, bool rewriteInPlace)
{
    private readonly Dictionary<string, (OperationStepStatus Status, int? Percent)> reported = [];

    // Step whose line is written without its line break yet, to be rewritten in place.
    private string? openStepKey;
    private int openLineLength;

    public void Report(OperationSnapshot snapshot)
    {
        foreach (OperationStepState step in snapshot.Steps)
        {
            if (step.Status == OperationStepStatus.Pending)
            {
                continue;
            }

            int? percent = rewriteInPlace && step.Key == snapshot.CurrentStep ? snapshot.Percent : null;
            if (this.reported.TryGetValue(step.Key, out var reported) && reported == (step.Status, percent))
            {
                continue;
            }
            this.reported[step.Key] = (step.Status, percent);
            Write(step, percent);
        }
    }

    /// <summary>
    /// Ends the line left open for a running step.
    /// </summary>
    public void Complete()
    {
        if (openStepKey is not null)
        {
            writer.WriteLine();
            openStepKey = null;
        }
    }

    public static string FormatLine(OperationStepState step, int? percent = null)
    {
        return step.Status switch
        {
            OperationStepStatus.Pending => $"[    ] {step.Title}",
            OperationStepStatus.Running => percent is int value
                ? string.Create(CultureInfo.InvariantCulture, $"[ .. ] {step.Title} {value}%")
                : $"[ .. ] {step.Title}",
            OperationStepStatus.Succeeded => $"[ ok ] {step.Title}",
            OperationStepStatus.Failed => step.Message is { Length: > 0 } message
                ? $"[fail] {step.Title}: {message}"
                : $"[fail] {step.Title}",
            OperationStepStatus.Skipped => $"[skip] {step.Title}",
            _ => $"[ ?? ] {step.Title}",
        };
    }

    private void Write(OperationStepState step, int? percent)
    {
        string line = FormatLine(step, percent);
        if (!rewriteInPlace)
        {
            writer.WriteLine(line);
            return;
        }

        if (openStepKey == step.Key)
        {
            writer.Write('\r');
            writer.Write(line.PadRight(openLineLength));
        }
        else
        {
            Complete();
            writer.Write(line);
        }

        if (step.Status == OperationStepStatus.Running)
        {
            openStepKey = step.Key;
            openLineLength = line.Length;
        }
        else
        {
            writer.WriteLine();
            openStepKey = null;
        }
    }
}
