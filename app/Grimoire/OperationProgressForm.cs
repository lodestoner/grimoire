namespace Spellbook;

/// <summary>A nonmodal cancellation surface that does not steal focus from the captured target.</summary>
internal sealed class OperationProgressForm : Form
{
    readonly Action _cancel;
    bool _finished;
    protected override bool ShowWithoutActivation => true;
    public OperationProgressForm(string message, Action cancel)
    {
        _cancel = cancel;
        Text = Brand.Name;
        Size = new Size(440, 135);
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        var label = new Label { Text = message, AutoEllipsis = true, Dock = DockStyle.Top, Height = 40, Padding = new Padding(10) };
        var button = new Button { Text = "Cancel", Dock = DockStyle.Bottom, Height = 32 };
        button.Click += (_, _) => { button.Enabled = false; label.Text = "Cancelling…"; _cancel(); };
        Controls.Add(label); Controls.Add(button);
        FormClosing += (_, e) => { if (!_finished) { _cancel(); e.Cancel = true; label.Text = "Cancelling…"; button.Enabled = false; } };
    }
    public void Finish() { _finished = true; Close(); }
}
