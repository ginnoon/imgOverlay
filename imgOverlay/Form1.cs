namespace imgOverlay
{
    public partial class Form1 : Form
    {
        private OverlayForm? overlay;

        public Form1()
        {
            InitializeComponent();
        }

        public void SetXOffsetMaximum(int m)
        {
            m = Math.Max(m, 0);
            trackBar2.Maximum = m;
            trackBar2.Value = Math.Min(m, trackBar2.Value);
            trackBar2_Scroll(trackBar2, new());
        }

        private void Form1_Load(object sender, EventArgs e)
        {
            overlay ??= new OverlayForm(this);
            overlay.Show();
        }

        private void Form1_FormClosed(object sender, FormClosedEventArgs e)
        {
            overlay?.Close();
            overlay?.Dispose();
        }

        private void textBox1_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox t) return;
            if (e.KeyCode == Keys.Enter)
            {
                overlay?.SetImage(t.Text);
                e.SuppressKeyPress = true;
            }
        }

        private void trackBar1_Scroll(object sender, EventArgs e)
        {
            if (sender is not TrackBar t) return;
            overlay?.ImageScale = (float)(t.Value * .1m);

            label1.Text = "scale: " + t.Value * .1m;
        }

        private void trackBar2_Scroll(object sender, EventArgs e)
        {
            if (sender is not TrackBar t) return;
            overlay?.XOffset = t.Value;

            label2.Text = "x offset: " + t.Value;
        }

        private void checkBox1_CheckedChanged(object sender, EventArgs e)
        {
            if (sender is not CheckBox s) return;
            overlay?.InvertX = s.Checked;
        }

        private void button1_Click(object sender, EventArgs e)
        {
            overlay?.SetImageFromClipboard();
        }
    }
}
