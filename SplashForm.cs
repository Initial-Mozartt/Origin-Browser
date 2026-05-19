using System.Drawing;
using System.Windows.Forms;

namespace OriginBrowser;

public partial class SplashForm : Form
{
    public SplashForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(400, 250);
        BackColor = Color.FromArgb(30, 30, 30);

        // Origin title
        var title = new Label
        {
            Text = "Origin",
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 22f, FontStyle.Bold),
            AutoSize = true
        };
        // We'll centre it manually
        title.Location = new Point(
            (ClientSize.Width - title.Width) / 2,
            (ClientSize.Height - title.Height) / 2 - 20);
        Controls.Add(title);

        // Loading label
        var loading = new Label
        {
            Text = "Loading…",
            ForeColor = Color.FromArgb(180, 180, 180),
            Font = new Font("Segoe UI", 10f, FontStyle.Regular),
            AutoSize = true
        };
        loading.Location = new Point(
            (ClientSize.Width - loading.Width) / 2,
            title.Bottom + 10);
        Controls.Add(loading);

        // Recentre when the form resizes (optional)
        Resize += (s, e) =>
        {
            title.Location = new Point(
                (ClientSize.Width - title.Width) / 2,
                (ClientSize.Height - title.Height) / 2 - 20);
            loading.Location = new Point(
                (ClientSize.Width - loading.Width) / 2,
                title.Bottom + 10);
        };
    }
}