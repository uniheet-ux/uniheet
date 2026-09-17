using System.Data;
using System.Drawing.Printing;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace UniversityLibraryPOS;

record Product(long Id, string Name, string Barcode, decimal Price);
record SaleItem(long ProductId, string Name, decimal Price, int Quantity);
record SaleRequest(string Customer, decimal Discount, List<SaleItem> Items);
record Sale(long Id, string InvoiceNo, string Customer, decimal Subtotal, decimal Discount, decimal Total, string CreatedAt, List<SaleItem>? Items = null);
sealed class AppConfig { public bool IsMain { get; set; } = true; public string ServerUrl { get; set; } = "http://127.0.0.1:5088"; public string ApiKey { get; set; } = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)); }

static class Paths {
    public static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "UniversityLibraryPOS");
    public static readonly string Config = Path.Combine(Root, "settings.json");
    public static readonly string Database = Path.Combine(Root, "sales.db");
    public static void Ensure() => Directory.CreateDirectory(Root);
}

static class ConfigStore {
    public static AppConfig Load() { Paths.Ensure(); if (!File.Exists(Paths.Config)) Save(new()); return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(Paths.Config)) ?? new(); }
    public static void Save(AppConfig c) { Paths.Ensure(); File.WriteAllText(Paths.Config, JsonSerializer.Serialize(c, new JsonSerializerOptions { WriteIndented = true })); }
}

sealed class Database {
    readonly string cs = $"Data Source={Paths.Database}";
    public Database() { Paths.Ensure(); using var c = Open(); using var cmd = c.CreateCommand(); cmd.CommandText = """
      CREATE TABLE IF NOT EXISTS products(id INTEGER PRIMARY KEY AUTOINCREMENT,name TEXT NOT NULL,barcode TEXT NOT NULL DEFAULT '',price REAL NOT NULL);
      CREATE UNIQUE INDEX IF NOT EXISTS ix_products_barcode ON products(barcode) WHERE barcode <> '';
      CREATE TABLE IF NOT EXISTS sales(id INTEGER PRIMARY KEY AUTOINCREMENT,invoice_no TEXT NOT NULL,customer TEXT NOT NULL,subtotal REAL NOT NULL,discount REAL NOT NULL,total REAL NOT NULL,created_at TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS sale_items(id INTEGER PRIMARY KEY AUTOINCREMENT,sale_id INTEGER NOT NULL,product_id INTEGER NOT NULL,name TEXT NOT NULL,price REAL NOT NULL,quantity INTEGER NOT NULL);
      """; cmd.ExecuteNonQuery(); Seed(c); }
    SqliteConnection Open() { var c = new SqliteConnection(cs); c.Open(); return c; }
    void Seed(SqliteConnection c) { using var n=c.CreateCommand(); n.CommandText="SELECT COUNT(*) FROM products"; if(Convert.ToInt32(n.ExecuteScalar())>0)return; foreach(var p in new[]{("دفتر 100 ورقة","1001",2000m),("قلم أزرق","1002",500m),("ورق A4 رزمة","1003",7000m),("طباعة ملونة","2001",1000m),("استنساخ ورقة","2002",250m)}) AddProduct(new(0,p.Item1,p.Item2,p.Item3)); }
    public List<Product> Products(){using var c=Open();using var x=c.CreateCommand();x.CommandText="SELECT id,name,barcode,price FROM products ORDER BY name";using var r=x.ExecuteReader();var a=new List<Product>();while(r.Read())a.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetDecimal(3)));return a;}
    public Product AddProduct(Product p){using var c=Open();using var x=c.CreateCommand();x.CommandText="INSERT INTO products(name,barcode,price) VALUES($n,$b,$p); SELECT last_insert_rowid();";x.Parameters.AddWithValue("$n",p.Name);x.Parameters.AddWithValue("$b",p.Barcode);x.Parameters.AddWithValue("$p",p.Price);return p with{Id=(long)(x.ExecuteScalar()??0L)};}
    public Sale AddSale(SaleRequest q){using var c=Open();using var tx=c.BeginTransaction();var sub=q.Items.Sum(i=>i.Price*i.Quantity);var total=Math.Max(0,sub-q.Discount);var now=DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");using var x=c.CreateCommand();x.Transaction=tx;x.CommandText="INSERT INTO sales(invoice_no,customer,subtotal,discount,total,created_at) VALUES('', $c,$s,$d,$t,$a); SELECT last_insert_rowid();";x.Parameters.AddWithValue("$c",string.IsNullOrWhiteSpace(q.Customer)?"زبون نقدي":q.Customer);x.Parameters.AddWithValue("$s",sub);x.Parameters.AddWithValue("$d",q.Discount);x.Parameters.AddWithValue("$t",total);x.Parameters.AddWithValue("$a",now);var id=(long)(x.ExecuteScalar()??0L);var no=$"U-{DateTime.Now:yyyyMMdd}-{id:00000}";using(var u=c.CreateCommand()){u.Transaction=tx;u.CommandText="UPDATE sales SET invoice_no=$n WHERE id=$i";u.Parameters.AddWithValue("$n",no);u.Parameters.AddWithValue("$i",id);u.ExecuteNonQuery();}foreach(var i in q.Items){using var y=c.CreateCommand();y.Transaction=tx;y.CommandText="INSERT INTO sale_items(sale_id,product_id,name,price,quantity) VALUES($s,$p,$n,$r,$q)";y.Parameters.AddWithValue("$s",id);y.Parameters.AddWithValue("$p",i.ProductId);y.Parameters.AddWithValue("$n",i.Name);y.Parameters.AddWithValue("$r",i.Price);y.Parameters.AddWithValue("$q",i.Quantity);y.ExecuteNonQuery();}tx.Commit();return new(id,no,string.IsNullOrWhiteSpace(q.Customer)?"زبون نقدي":q.Customer,sub,q.Discount,total,now,q.Items);}
    public List<Sale> Sales(){using var c=Open();using var x=c.CreateCommand();x.CommandText="SELECT id,invoice_no,customer,subtotal,discount,total,created_at FROM sales ORDER BY id DESC LIMIT 500";using var r=x.ExecuteReader();var a=new List<Sale>();while(r.Read())a.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2),r.GetDecimal(3),r.GetDecimal(4),r.GetDecimal(5),r.GetString(6)));return a;}
}

sealed class ApiServer {
    readonly HttpListener h=new(); readonly Database db; readonly string key; CancellationTokenSource cts=new();
    public ApiServer(Database d,string k){db=d;key=k;h.Prefixes.Add("http://+:5088/");}
    public void Start(){try{h.Start();_=Loop();}catch(Exception e){MessageBox.Show("تعذر تشغيل خادم الجهاز الرئيسي:\n"+e.Message,"تنبيه");}}
    async Task Loop(){while(!cts.IsCancellationRequested){HttpListenerContext c;try{c=await h.GetContextAsync();}catch{return;} _=Task.Run(()=>Handle(c));}}
    async Task Handle(HttpListenerContext c){try{if(c.Request.Headers["X-API-Key"]!=key){c.Response.StatusCode=401;return;}var p=c.Request.Url?.AbsolutePath??"/";object result;if(p=="/health")result=new{ok=true};else if(p=="/products"&&c.Request.HttpMethod=="GET")result=db.Products();else if(p=="/products"&&c.Request.HttpMethod=="POST")result=db.AddProduct((await JsonSerializer.DeserializeAsync<Product>(c.Request.InputStream,new JsonSerializerOptions{PropertyNameCaseInsensitive=true}))!);else if(p=="/sales"&&c.Request.HttpMethod=="GET")result=db.Sales();else if(p=="/sales"&&c.Request.HttpMethod=="POST")result=db.AddSale((await JsonSerializer.DeserializeAsync<SaleRequest>(c.Request.InputStream,new JsonSerializerOptions{PropertyNameCaseInsensitive=true}))!);else{c.Response.StatusCode=404;return;}c.Response.ContentType="application/json; charset=utf-8";await JsonSerializer.SerializeAsync(c.Response.OutputStream,result);}catch(Exception e){c.Response.StatusCode=500;var b=Encoding.UTF8.GetBytes(e.Message);await c.Response.OutputStream.WriteAsync(b);}finally{c.Response.Close();}}
}

sealed class ApiClient {
    readonly HttpClient h=new(); public string BaseUrl {get;private set;} public ApiClient(AppConfig c){BaseUrl=c.ServerUrl.TrimEnd('/');h.DefaultRequestHeaders.Add("X-API-Key",c.ApiKey);}
    public async Task<bool> Health(){try{return (await h.GetAsync(BaseUrl+"/health")).IsSuccessStatusCode;}catch{return false;}}
    public async Task<List<Product>> Products()=>await h.GetFromJsonAsync<List<Product>>(BaseUrl+"/products")??[];
    public async Task<Product> AddProduct(Product p){var r=await h.PostAsJsonAsync(BaseUrl+"/products",p);r.EnsureSuccessStatusCode();return(await r.Content.ReadFromJsonAsync<Product>())!;}
    public async Task<Sale> AddSale(SaleRequest q){var r=await h.PostAsJsonAsync(BaseUrl+"/sales",q);r.EnsureSuccessStatusCode();return(await r.Content.ReadFromJsonAsync<Sale>())!;}
    public async Task<List<Sale>> Sales()=>await h.GetFromJsonAsync<List<Sale>>(BaseUrl+"/sales")??[];
}

sealed class MainForm:Form {
    readonly AppConfig config; ApiClient api; readonly List<SaleItem> cart=[]; List<Product> products=[];
    readonly DataGridView productGrid=Grid(),cartGrid=Grid(),salesGrid=Grid(); readonly TextBox customer=new(){PlaceholderText="اسم الزبون (اختياري)"},discount=new(){Text="0"},search=new(){PlaceholderText="ابحث بالاسم أو الباركود"}; readonly Label total=new(){AutoSize=true,Font=new("Tahoma",16,FontStyle.Bold),ForeColor=Color.FromArgb(23,37,84)}; readonly Label status=new(){AutoSize=true};
    public MainForm(AppConfig c){config=c;api=new(c);Text="مكتبة الجامعة - المبيعات والفواتير";WindowState=FormWindowState.Maximized;MinimumSize=new(1000,650);Font=new("Tahoma",10);RightToLeft=RightToLeft.Yes;RightToLeftLayout=true;BackColor=Color.FromArgb(248,250,252);Build();Shown+=async(_,_)=>await ReloadAll();}
    static DataGridView Grid()=>new(){Dock=DockStyle.Fill,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill,ReadOnly=true,AllowUserToAddRows=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,BackgroundColor=Color.White,BorderStyle=BorderStyle.None,RowHeadersVisible=false};
    Button Btn(string t,Color c)=>new(){Text=t,BackColor=c,ForeColor=Color.White,FlatStyle=FlatStyle.Flat,Height=42,Dock=DockStyle.Top,Margin=new(5)};
    void Build(){var tabs=new TabControl{Dock=DockStyle.Fill};tabs.TabPages.Add(SaleTab());tabs.TabPages.Add(ProductsTab());tabs.TabPages.Add(SalesTab());tabs.TabPages.Add(SettingsTab());var head=new Panel{Dock=DockStyle.Top,Height=64,BackColor=Color.FromArgb(23,37,84)};var title=new Label{Text="مكتبة الجامعة | نظام المبيعات",ForeColor=Color.White,Font=new("Tahoma",18,FontStyle.Bold),AutoSize=true,Location=new(24,16)};status.ForeColor=Color.White;status.Location=new(760,22);head.Controls.Add(title);head.Controls.Add(status);Controls.Add(tabs);Controls.Add(head);}
    TabPage SaleTab(){var page=new TabPage("فاتورة بيع جديدة");var split=new SplitContainer{Dock=DockStyle.Fill,SplitterDistance=520};var lp=new Panel{Dock=DockStyle.Fill,Padding=new(12)};search.Dock=DockStyle.Top;search.Height=38;search.TextChanged+=(_,_)=>ShowProducts();productGrid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0&&productGrid.Rows[e.RowIndex].Tag is Product p)AddCart(p);};lp.Controls.Add(productGrid);lp.Controls.Add(search);split.Panel1.Controls.Add(lp);var rp=new Panel{Dock=DockStyle.Fill,Padding=new(12)};var save=Btn("حفظ وطباعة فاتورة A4",Color.FromArgb(236,72,153));save.Click+=async(_,_)=>await SaveSale();var clear=Btn("مسح الفاتورة",Color.FromArgb(100,116,139));clear.Click+=(_,_)=>{cart.Clear();ShowCart();};var info=new FlowLayoutPanel{Dock=DockStyle.Top,Height=95,FlowDirection=FlowDirection.TopDown,WrapContents=false};customer.Width=400;discount.Width=180;info.Controls.Add(customer);info.Controls.Add(new Label{Text="الخصم بالدينار",AutoSize=true});info.Controls.Add(discount);var foot=new Panel{Dock=DockStyle.Bottom,Height=150};total.Location=new(10,10);save.Location=new(0,45);clear.Location=new(0,92);foot.Controls.Add(total);foot.Controls.Add(save);foot.Controls.Add(clear);cartGrid.CellDoubleClick+=(_,e)=>{if(e.RowIndex>=0){cart.RemoveAt(e.RowIndex);ShowCart();}};rp.Controls.Add(cartGrid);rp.Controls.Add(info);rp.Controls.Add(foot);split.Panel2.Controls.Add(rp);page.Controls.Add(split);return page;}
    TabPage ProductsTab(){var p=new TabPage("الأصناف والأسعار");var add=Btn("إضافة صنف جديد",Color.FromArgb(236,72,153));add.Click+=async(_,_)=>await AddProduct();var g=Grid();g.Name="ProductsManage";p.Controls.Add(g);p.Controls.Add(add);return p;}
    TabPage SalesTab(){var p=new TabPage("سجل الفواتير");var refresh=Btn("تحديث السجل",Color.FromArgb(23,37,84));refresh.Click+=async(_,_)=>await LoadSales();p.Controls.Add(salesGrid);p.Controls.Add(refresh);return p;}
    TabPage SettingsTab(){var p=new TabPage("الإعدادات");var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,Padding=new(30),WrapContents=false};var main=new CheckBox{Text="هذا هو الجهاز الرئيسي",Checked=config.IsMain,AutoSize=true};var url=new TextBox{Text=config.ServerUrl,Width=430};var key=new TextBox{Text=config.ApiKey,Width=430};var save=Btn("حفظ الإعدادات وإعادة تشغيل البرنامج",Color.FromArgb(23,37,84));save.Width=430;save.Click+=(_,_)=>{config.IsMain=main.Checked;config.ServerUrl=url.Text.Trim();config.ApiKey=key.Text.Trim();ConfigStore.Save(config);MessageBox.Show("تم الحفظ. أغلقي البرنامج وافتحيه مجددًا.");};panel.Controls.Add(main);panel.Controls.Add(new Label{Text="عنوان الجهاز الرئيسي (مثال: http://192.168.1.10:5088)",AutoSize=true});panel.Controls.Add(url);panel.Controls.Add(new Label{Text="رمز الاتصال المشترك بين الجهازين",AutoSize=true});panel.Controls.Add(key);panel.Controls.Add(save);p.Controls.Add(panel);return p;}
    async Task ReloadAll(){status.Text=await api.Health()?"● متصل بالجهاز الرئيسي":"● غير متصل";try{products=await api.Products();ShowProducts();ShowCart();await LoadSales();ShowManage();}catch(Exception e){MessageBox.Show("تعذر الاتصال: "+e.Message);}}
    void ShowProducts(){var q=search.Text.Trim();productGrid.DataSource=products.Where(x=>x.Name.Contains(q)||x.Barcode.Contains(q)).Select(x=>new{الصنف=x.Name,الباركود=x.Barcode,السعر=x.Price,Source=x}).ToList();productGrid.Columns["Source"].Visible=false;foreach(DataGridViewRow r in productGrid.Rows)r.Tag=((dynamic)r.DataBoundItem).Source;}
    void AddCart(Product p){var i=cart.FindIndex(x=>x.ProductId==p.Id);if(i>=0)cart[i]=cart[i] with{Quantity=cart[i].Quantity+1};else cart.Add(new(p.Id,p.Name,p.Price,1));ShowCart();}
    void ShowCart(){cartGrid.DataSource=cart.Select(x=>new{الصنف=x.Name,الكمية=x.Quantity,السعر=x.Price,الإجمالي=x.Price*x.Quantity}).ToList();var sub=cart.Sum(x=>x.Price*x.Quantity);decimal.TryParse(discount.Text,out var d);total.Text=$"الإجمالي: {Math.Max(0,sub-d):N0} د.ع";}
    async Task SaveSale(){if(cart.Count==0){MessageBox.Show("أضيفي صنفًا إلى الفاتورة");return;}decimal.TryParse(discount.Text,out var d);try{var sale=await api.AddSale(new(customer.Text,d,[..cart]));Print(sale);cart.Clear();customer.Clear();discount.Text="0";ShowCart();await LoadSales();}catch(Exception e){MessageBox.Show("تعذر حفظ الفاتورة: "+e.Message);}}
    void Print(Sale s){var doc=new PrintDocument();doc.DefaultPageSettings.PaperSize=new PaperSize("A4",827,1169);doc.PrintPage+=(_,e)=>{var g=e.Graphics;using var f=new Font("Tahoma",11);using var b=new Font("Tahoma",18,FontStyle.Bold);g.DrawString("مكتبة الجامعة",b,Brushes.Navy,560,50);g.DrawString("للتقديم الإلكتروني والمستلزمات المدرسية",f,Brushes.Black,470,90);g.DrawString($"فاتورة: {s.InvoiceNo}",f,Brushes.Black,50,60);g.DrawString($"التاريخ: {s.CreatedAt}",f,Brushes.Black,50,90);g.DrawString($"الزبون: {s.Customer}",f,Brushes.Black,50,120);var y=175;g.DrawLine(Pens.Black,40,y,780,y);y+=25;foreach(var i in s.Items??[]){g.DrawString($"{i.Name}    {i.Quantity} × {i.Price:N0} = {i.Price*i.Quantity:N0} د.ع",f,Brushes.Black,70,y);y+=32;}g.DrawLine(Pens.Black,40,y,780,y);y+=25;g.DrawString($"المجموع: {s.Subtotal:N0} د.ع",f,Brushes.Black,500,y);y+=28;g.DrawString($"الخصم: {s.Discount:N0} د.ع",f,Brushes.Black,500,y);y+=35;g.DrawString($"المبلغ النهائي: {s.Total:N0} د.ع",b,Brushes.Navy,400,y);};using var dlg=new PrintPreviewDialog{Document=doc,WindowState=FormWindowState.Maximized};dlg.ShowDialog();}
    async Task AddProduct(){using var f=new Form{Text="إضافة صنف",Size=new(430,310),RightToLeft=RightToLeft.Yes,RightToLeftLayout=true};var n=new TextBox{PlaceholderText="اسم الصنف",Width=340,Top=30,Left=30};var b=new TextBox{PlaceholderText="الباركود",Width=340,Top=80,Left=30};var pr=new NumericUpDown{Maximum=100000000,ThousandsSeparator=true,Width=340,Top=130,Left=30};var ok=Btn("حفظ",Color.FromArgb(236,72,153));ok.Width=340;ok.Top=185;ok.Left=30;ok.Dock=DockStyle.None;ok.Click+=async(_,_)=>{if(string.IsNullOrWhiteSpace(n.Text)){MessageBox.Show("أدخلي اسم الصنف");return;}await api.AddProduct(new(0,n.Text.Trim(),b.Text.Trim(),pr.Value));f.DialogResult=DialogResult.OK;f.Close();};f.Controls.AddRange([n,b,pr,ok]);if(f.ShowDialog()==DialogResult.OK){products=await api.Products();ShowProducts();ShowManage();}}
    void ShowManage(){var g=Controls.Find("ProductsManage",true).FirstOrDefault() as DataGridView;if(g!=null)g.DataSource=products.Select(x=>new{الصنف=x.Name,الباركود=x.Barcode,السعر=x.Price}).ToList();}
    async Task LoadSales(){try{salesGrid.DataSource=(await api.Sales()).Select(x=>new{رقم_الفاتورة=x.InvoiceNo,الزبون=x.Customer,الإجمالي=x.Total,التاريخ=x.CreatedAt}).ToList();}catch{}}
}

static class Program {
    [STAThread] static void Main(){ApplicationConfiguration.Initialize();var c=ConfigStore.Load();if(c.IsMain){var db=new Database();new ApiServer(db,c.ApiKey).Start();}Application.Run(new MainForm(c));}
}
