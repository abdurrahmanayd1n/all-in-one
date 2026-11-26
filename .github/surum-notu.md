**AllInOne.exe** dosyasını indir, kalıcı bir klasöre koy, çift tıkla. Saatin yanında simgesi görünür.

### Bu sürümde

**Ekran (`Alt+S`)**
- Ekran görüntüsü: panoya kopyalanır ve `Resimler\All In One` klasörüne kaydedilir.
- Ekran kaydı: bilgisayar sesi ve mikrofon ayrı ayrı açılır.
- Görüntüyü ekrana sabitleme ve kayan (uzun sayfa) görüntü.

**Dosyalar**
- `Alt+Space`: seçili dosyanın Dosya Gezgini görünümünde önizlemesi (görsel, metin, PDF, Word, ZIP, video, ses).
- Video kesme ve birleştirme.
- PDF birleştirme, sayfa alma ve ayırma (qpdf gerekir).
- Klasörde kopya dosya bulma ve türe ya da aya göre düzenleme (geri alınabilir).
- Doğrulama kodu (SHA-256, SHA-1, MD5) gösterme ve karşılaştırma.

**Diğer**
- `Alt+T`: tablo önce düzeltmen için açılır; CSV olarak da kaydedilir.
- Kısayol tuşları Ayarlar'dan değiştirilir.
- Arayüz Türkçe ve İngilizce.
- **Windows 10** (22H2) desteği: bildirimler saatin yanındaki simgeden gelir.
- Bazı Windows sürümlerinde görsel açılırken oluşan hata düzeltildi.

### Bilmen gerekenler

- Windows 11 ya da Windows 10 (22H2), 64 bit.
- Sürüm henüz **imzalı değil**: "bilinmeyen yayıncı" uyarısı çıkabilir (**Ek bilgi → Yine de çalıştır**). **Akıllı Uygulama Denetimi** açık bilgisayarlar engeller. Ayrıntı: [Code signing policy](https://github.com/abdurrahmanayd1n/all-in-one#code-signing-policy).
- Video araçları, ekran kaydı ve WebP için FFmpeg gerekir: `winget install Gyan.FFmpeg`
- PDF araçları için qpdf gerekir: `winget install QPDF.QPDF`
- `Alt+S` bazı programlarda kullanılır (Outlook'ta gönder); Ayarlar'dan değiştirebilirsin.
- Windows ile birlikte açılsın istersen: simgeye çift tıkla → Ayarlar.
