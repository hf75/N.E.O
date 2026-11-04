using System.Text.Json;

// A stand-in for an old program nobody has the source of. Start it with "--version 2" for the next version: a label
// and two buttons are called differently, and the booking fields sit in a group. The stock is kept in a file, so that
// it is the same after the update.
var version2 = args.Contains("--version") && args.SkipWhile(a => a != "--version").Skip(1).FirstOrDefault() == "2";
var reset = args.Contains("--reset");
ApplicationConfiguration.Initialize();
Application.Run(new StockroomForm(version2, reset));

internal sealed record Item(string No, string Name, int Stock, string Place);

internal sealed class StockroomForm : Form
{
    private static readonly string DataFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Stockroom2003", "stock.json");

    private readonly List<Item> _items;
    private readonly TextBox _item = new() { Width = 110 };
    private readonly TextBox _quantity = new() { Width = 60 };
    private readonly Label _name = new() { AutoSize = true, Text = "–" };
    private readonly Label _stock = new() { AutoSize = true, Text = "–" };
    private readonly ListView _list = new() { View = View.Details, FullRowSelect = true, GridLines = true, AccessibleName = "Items" };
    private readonly ToolStripStatusLabel _status = new() { Name = "status", Text = "Ready.", Spring = true, TextAlign = ContentAlignment.MiddleLeft };

    public StockroomForm(bool version2, bool reset)
    {
        _items = LoadStock(reset);
        Text = version2 ? "Stockroom 2004" : "Stockroom 2003";
        Font = new Font("Tahoma", 9f);
        ClientSize = new Size(560, 430);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(120, 120);

        var lookUp = new Button { Text = "Look up", AutoSize = true };
        var bookIn = new Button { Text = version2 ? "Receive" : "Book in", AutoSize = true };
        var bookOut = new Button { Text = version2 ? "Issue" : "Book out", AutoSize = true };
        lookUp.Click += (_, _) => LookUp();
        bookIn.Click += (_, _) => Book(+1);
        bookOut.Click += (_, _) => Book(-1);
        _item.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) LookUp(); };

        // Labels go right before their fields: that is what names a field for the accessibility interface.
        var fields = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Padding = new Padding(6) };
        fields.Controls.Add(new Label { Text = "Item no.:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        fields.Controls.Add(_item, 1, 0);
        fields.Controls.Add(lookUp, 2, 0);
        fields.Controls.Add(new Label { Text = "Name:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 1);
        fields.Controls.Add(_name, 1, 1);
        fields.Controls.Add(new Label { Text = "In stock:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 2);
        fields.Controls.Add(_stock, 1, 2);
        fields.Controls.Add(new Label { Text = version2 ? "Amount:" : "Quantity:", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 3);
        fields.Controls.Add(_quantity, 1, 3);
        var buttons = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        buttons.Controls.Add(bookIn);
        buttons.Controls.Add(bookOut);
        fields.Controls.Add(buttons, 1, 4);

        Control booking = fields;
        if (version2)
        {
            var group = new GroupBox { Text = "Booking", AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(8) };
            fields.Dock = DockStyle.Fill;
            group.Controls.Add(fields);
            booking = group;
        }
        else fields.Dock = DockStyle.Top;

        _list.Columns.Add("Item no.", 80);
        _list.Columns.Add("Name", 220);
        _list.Columns.Add("In stock", 80, HorizontalAlignment.Right);
        _list.Columns.Add("Place", 100);
        _list.Dock = DockStyle.Fill;
        _list.SelectedIndexChanged += (_, _) => { if (_list.SelectedItems.Count == 1) { _item.Text = _list.SelectedItems[0].Text; LookUp(); } };

        var statusBar = new StatusStrip();
        statusBar.Items.Add(_status);

        Controls.Add(_list);
        Controls.Add(booking);
        Controls.Add(statusBar);
        FillList();
    }

    private void LookUp()
    {
        var item = Find();
        _name.Text = item?.Name ?? "–";
        _stock.Text = item?.Stock.ToString() ?? "–";
        _status.Text = item == null ? $"No item {_item.Text.Trim()}." : "Ready.";
    }

    private void Book(int direction)
    {
        var item = Find();
        if (item == null)
        {
            MessageBox.Show(this, $"There is no item {_item.Text.Trim()}.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (!int.TryParse(_quantity.Text.Trim(), out var quantity) || quantity <= 0)
        {
            MessageBox.Show(this, "Enter a quantity above 0.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (direction < 0 && quantity > item.Stock)
        {
            MessageBox.Show(this, $"Not enough stock of {item.No}: {item.Stock} left.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var booked = item with { Stock = item.Stock + direction * quantity };
        _items[_items.IndexOf(item)] = booked;
        Save();
        FillList();
        LookUp();
        _status.Text = direction < 0
            ? $"Booked {quantity} x {item.No} out. Now in stock: {booked.Stock}."
            : $"Booked {quantity} x {item.No} in. Now in stock: {booked.Stock}.";
    }

    private Item? Find() => _items.FirstOrDefault(i => i.No == _item.Text.Trim());

    private void FillList()
    {
        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var item in _items)
            _list.Items.Add(new ListViewItem([item.No, item.Name, item.Stock.ToString(), item.Place]));
        _list.EndUpdate();
    }

    private static List<Item> LoadStock(bool reset)
    {
        try
        {
            if (!reset && File.Exists(DataFile))
                return JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(DataFile)) ?? Sample();
        }
        catch (Exception) { /* a broken file starts over */ }
        return Sample();
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DataFile)!);
        File.WriteAllText(DataFile, JsonSerializer.Serialize(_items));
    }

    // Made up.
    private static List<Item> Sample() =>
    [
        new("4711", "Copper pipe 15 mm", 17, "A-03"),
        new("4712", "Brass elbow 15 mm", 42, "A-04"),
        new("4713", "Ball valve 1/2\"", 8, "B-01"),
        new("4720", "Pipe clamp 15 mm", 120, "B-07"),
        new("4731", "Thread seal tape", 35, "C-02"),
        new("4740", "Radiator valve", 6, "C-05"),
    ];
}
