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
    // By position of the step: the same step can be in an operation more than once.
    private readonly Dictionary<int, (OperationStepStatus Status, int? Percent)> reported = [];

    // Position of the step whose line is written without its line break yet, to be rewritten in place.
    private int? openStepIndex;
    private int openLineLength;

    public void Report(OperationSnapshot snapshot)
    {
        for (int index = 0; index < snapshot.Steps.Count; index++)
        {
            OperationStepState step = snapshot.Steps[index];
            if (step.Status == OperationStepStatus.Pending)
            {
                continue;
            }

            int? percent = rewriteInPlace && index == snapshot.CurrentStepIndex ? snapshot.Percent : null;
            if (this.reported.TryGetValue(index, out var reported) && reported == (step.Status, percent))
            {
                continue;
            }
            this.reported[index] = (step.Status, percent);
            Write(index, step, percent);
        }
    }

    /// <summary>
    /// Ends the line left open for a running step.
    /// </summary>
    public void Complete()
    {
        if (openStepIndex is not null)
        {
            writer.WriteLine();
            openStepIndex = null;
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

    private void Write(int index, OperationStepState step, int? percent)
    {
        string line = FormatLine(step, percent);
        if (!rewriteInPlace)
        {
            writer.WriteLine(line);
            return;
        }

        if (openStepIndex == index)
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
            openStepIndex = index;
            openLineLength = line.Length;
        }
        else
        {
            writer.WriteLine();
            openStepIndex = null;
        }
    }
}
