# All In One

Windows için İsviçre çakısı: Dosya Gezgini'nde ya da masaüstünde dosya seç, **Sol Alt** tuşunu basılı tut, imlecin yanında radyal menü açılsın. Yanında ekran **renk seçici** ve birkaç küçük araç gelir. Her şey bilgisayarında çalışır; internete bağlanmaz.

*A radial menu for files selected in Explorer (hold Left Alt), a file preview (Alt+Space), screenshots and screen recording (Alt+S), a colour picker and small Windows tools. The interface is in Turkish and English.*

## İndir ve çalıştır

1. **[AllInOne.exe dosyasını indir](https://github.com/abdurrahmanayd1n/all-in-one/releases/latest/download/AllInOne.exe)** (tüm sürümler: [Releases](https://github.com/abdurrahmanayd1n/all-in-one/releases)).
2. Kalıcı duracağı bir klasöre koy ve çift tıkla. Saatin yanında simgesi görünür.
3. Windows ile birlikte açılsın istersen: simgeye çift tıkla → **Ayarlar** → otomatik başlatmayı işaretle.

Gereken: Windows 11 ya da Windows 10 (22H2), 64 bit; ayrıca bir şey kurman gerekmez. Windows 10'da bildirimler saatin yanındaki simgeden gelir. Windows 10 desteği, ona en yakın sunucu sürümünde (Windows Server 2022) otomatik olarak derlenip sınanır; masaüstü Windows 10'da henüz elle denenmedi.

**Sürümler henüz imzalı değil** (bkz. [Code signing policy](#code-signing-policy)). Bu yüzden:

- Windows ilk açılışta "bilinmeyen yayıncı" uyarısı gösterebilir (**Ek bilgi → Yine de çalıştır**).
- **Akıllı Uygulama Denetimi** açık bilgisayarlar uygulamayı engeller ve tek bir uygulama için izin verilemez. Kaynaktan derlenen exe de engellenir. İmza gelene kadar bu bilgisayarlarda uygulama çalışmaz.

Kaldırmak için: simgeye çift tıkla → **Ayarlar** → **Uygulamayı kaldır**, sonra açılan klasörde seçili dosyayı sil.

## Kaynaktan kurulum

Uygulama, Windows ile gelen C# derleyicisiyle senin bilgisayarında derlenir.

1. Bu sayfada **Code → Download ZIP** ile indir ve klasöre çıkar (ya da `git clone`).
2. `kur.cmd` dosyasına çift tıkla.

Betik uygulamayı derler, kendi testlerini çalıştırır, `%LOCALAPPDATA%\AllInOne` klasörüne kurar, Windows ile birlikte açılacak şekilde ayarlar ve başlatır.

Kaldırmak için, klasörde:

```powershell
.\kur.cmd -Kaldir
```

### İsteğe bağlı

- **FFmpeg**: video araçları, ekran kaydı ve WebP için gerekir. Yoksa bu seçenekler görünmez.

  ```powershell
  winget install Gyan.FFmpeg
  ```

  Sonra uygulamayı kapatıp yeniden aç.
- **qpdf**: PDF birleştirme ve sayfa ayırma için gerekir. Yoksa bu seçenekler görünmez.

  ```powershell
  winget install QPDF.QPDF
  ```

  Sonra uygulamayı kapatıp yeniden aç.
- **Metin tanıma**: Windows'ta yüklü dil paketlerini kullanır (Ayarlar → Zaman ve dil → Dil).

## Radyal menü

Dosyayı seç, Sol Alt'ı basılı tut, istediğin dilimin üzerinde tuşu bırak (ya da tıkla). Alt seçenekler için dışa doğru kaydır. `Esc` kapatır. Seçili dosya yoksa Alt her zamanki gibi çalışır.

| Seçtiğin | Yapabildiklerin |
| --- | --- |
| Görsel | Format değiştir (JPG, PNG, WebP, BMP, TIFF, GIF), ölçeklendir, kırp, sıkıştır (akıllı kalite veya hedef boyut, önce/sonra karşılaştırma), metni al, hazır ayar |
| Video | Sıkıştır, kes (hızlı ve kayıpsız ya da kare hassasiyetli), birkaç videoyu birleştir, GIF yap, sesi ayır (MP3, M4A, WAV) |
| PDF | Sırasını seçip birleştir, seçtiğin sayfaları ya da her sayfayı ayrı dosya yap |
| Klasör | Boyut dökümü, ZIP yap, içindekileri toplu adlandır, türe veya aya göre alt klasörlere ayır (geri alınabilir), aynı içerikli kopyaları bulup Geri Dönüşüm Kutusu'na at |
| Her dosya | Yolu kopyala, terminal aç, toplu yeniden adlandır (geri alınabilir), "Kim kullanıyor?", doğrulama kodu (SHA-256, SHA-1, MD5) göster ve karşılaştır |

Görsel ve video işlemleri yeni dosya yazar; asıl dosyanın üzerine yazılmaz. Birden çok dosya seçersen işlem hepsine uygulanır.

## Dosya önizleme

Dosya seç, **Alt+Space** bas: Dosya Gezgini görünümünde bir pencere açılır. Görsel, metin, PDF, Word belgesi, ZIP listesi, video ve ses içeriği görünür; klasör ve diğer dosyalar simgesiyle gösterilir. Hiçbir şey değiştirilmez. `Esc` ya da `Boşluk` kapatır, `← →` seçili diğer dosyaya geçer. Seçili dosya yoksa Alt+Space başka programlara kalır. Ayarlar'dan kapatılabilir.

## Ekran görüntüsü ve kaydı

**Alt+S**, üstteki çubuktan türü seç, sonra alanı sürükle (`Tab` türler arasında geçer, `Enter` tüm ekranı alır):

- **Görüntü**: panoya kopyalanır ve `Resimler\All In One` klasörüne kaydedilir.
- **Video**: `Videolar\All In One` klasörüne kaydedilir; bilgisayar sesi ve mikrofon ayrı ayrı açılıp kapanır. Bitirmek için `Durdur` ya da yine Alt+S.
- **Sabitle**: seçtiğin parça üstte duran küçük bir pencere olur. Sürükle, tekerlekle büyüt ya da küçült, çift tıkla veya `Esc` ile kapat; sağ tıkta kopyala ve kaydet.
- **Kayan**: yalnız kayan kısmı seç; uygulama sayfayı fare tekerleğiyle kaydırıp tek uzun görüntü yapar. `Bitir`, `Esc` ya da Alt+S durdurur. Kendiliğinden hareket eden içerikte (video, canlandırma) birleştirme yarıda kalabilir.

## Renk seçici

Saatin yanındaki ayrı simge:

- **Sol tık**: ekranda bir noktaya tıkla, rengi panoya kopyalansın (ok tuşlarıyla piksel piksel ilerlenir).
- **Sağ tık**: daha önce aldığın renkler.

## Kısayollar

| Kısayol | İşlev |
| --- | --- |
| `Alt+S` | Ekran görüntüsü ya da ekran kaydı |
| `Alt+Space` | Seçili dosyayı önizle |
| `Alt+Q` veya `Win+Shift+O` | Ekranda alan seç, içindeki metni kopyala |
| `Alt+T` | Ekrandaki tabloyu seç; düzeltip kopyala (Excel'e yapıştır) ya da CSV kaydet |
| `Win+Shift+U` | Pencereyi üstte sabitle / bırak |
| `Win+Shift+Y` | Düz (biçimsiz) yapıştır |
| `Win+Shift+B` | Büyük/küçük harf çevirip yapıştır |

Menünün açılma süresi, kısayol tuşları, arayüz dili (Türkçe / İngilizce), otomatik başlatma ve dosya adlarına eklenecek sabit ek, saatin yanındaki All In One simgesinden **Ayarlar** ile değişir. Bir kısayol başka bir programdakiyle çakışırsa (ör. Outlook'ta Alt+S gönder demektir) buradan değiştir.

## Code signing policy

Releases are **not signed yet**. This project intends to apply to the SignPath Foundation program; once accepted, releases will carry: *Free code signing provided by [SignPath.io](https://about.signpath.io), certificate by [SignPath Foundation](https://signpath.org)*.

- Committers, reviewers and approvers: [abdurrahmanayd1n](https://github.com/abdurrahmanayd1n)
- Release binaries are built only from this repository by [GitHub Actions](.github/workflows/surum.yml), from a version tag.
- Privacy policy: This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. See [Gizlilik](#gizlilik).
- Optional third-party programs: [FFmpeg](https://ffmpeg.org) and [qpdf](https://qpdf.sourceforge.io), installed by the user and run locally.

## Gizlilik

- Uygulama ağ bağlantısı açmaz; hiçbir bilgi başka bir sisteme gönderilmez.
- Sol Alt'ı fark edebilmek için bir klavye kancası kullanır; bastığın tuşları kaydetmez. Bazı güvenlik yazılımları klavye kancalarından uyarı verebilir.
- Ayarlar ve renk geçmişi `HKCU\Software\AllInOne` altında, hata kaydı `%LOCALAPPDATA%\AllInOne\hata.log` dosyasındadır. Hata kaydında dosya yolları geçebilir; birine göndermeden önce bak.
- Sistemde değiştirdikleri, hepsi yalnız senin kullanıcı hesabında: yukarıdaki iki yer, bildirimlerin adı ve simgesi (`HKCU\Software\Classes\AppUserModelId\AllInOne.App`, `%LOCALAPPDATA%\AllInOne\app.ico`) ve sen açarsan otomatik başlatma kaydı. **Uygulamayı kaldır** hepsini siler.

## Geliştirme

Kaynak, tek klasörde düz C# dosyalarıdır (C# 5, WinForms döngüsü + WPF pencereleri). `kur.ps1` derler ve `AllInOne.exe --test` ile öz testi çalıştırır. Yeni menü araçları `AllInOne.cs` içindeki `Tools` sınıfına eklenir.
