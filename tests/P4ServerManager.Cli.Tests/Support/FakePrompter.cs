using P4ServerManager.Cli.Prompts;

namespace P4ServerManager.Cli.Tests.Support;

internal sealed class FakePrompter : IPrompter
{
    public bool IsInteractive { get; set; }

    /// <summary>
    /// Answers to <see cref="ReadSecret"/>, in turn.
    /// </summary>
    public Queue<string> Secrets { get; } = new();

    public bool ConfirmAnswer { get; set; }

    public List<string> Questions { get; } = [];

    public string ReadSecret(string prompt)
    {
        Questions.Add(prompt);
        return Secrets.Dequeue();
    }

    public bool Confirm(string question)
    {
        Questions.Add(question);
        return ConfirmAnswer;
    }
}
