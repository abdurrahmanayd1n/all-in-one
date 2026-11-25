# All In One

Windows için İsviçre çakısı: Dosya Gezgini'nde ya da masaüstünde dosya seç, **Sol Alt** tuşunu basılı tut, imlecin yanında radyal menü açılsın. Yanında ekran **renk seçici** ve birkaç küçük araç gelir. Her şey bilgisayarında çalışır; internete bağlanmaz.

*A radial menu for files selected in Explorer (hold Left Alt), plus a screen colour picker and small Windows tools. The interface is in Turkish.*

## Kurulum

Gereken: Windows 11 (64 bit). Ayrıca bir şey kurman gerekmez; uygulama, Windows ile gelen C# derleyicisiyle senin bilgisayarında derlenir.

1. Bu sayfada **Code → Download ZIP** ile indir ve klasöre çıkar (ya da `git clone`).
2. `kur.cmd` dosyasına çift tıkla.

Betik uygulamayı derler, kendi testlerini çalıştırır, `%LOCALAPPDATA%\AllInOne` klasörüne kurar, Windows ile birlikte açılacak şekilde ayarlar ve başlatır.

Kaldırmak için, klasörde:

```powershell
.\kur.cmd -Kaldir
```

### İsteğe bağlı

- **FFmpeg**: video araçları ve WebP için gerekir. Yoksa bu seçenekler görünmez.

  ```powershell
  winget install Gyan.FFmpeg
  ```

  Sonra uygulamayı kapatıp yeniden aç.
- **Metin tanıma**: Windows'ta yüklü dil paketlerini kullanır (Ayarlar → Zaman ve dil → Dil).

## Radyal menü

Dosyayı seç, Sol Alt'ı basılı tut, istediğin dilimin üzerinde tuşu bırak (ya da tıkla). Alt seçenekler için dışa doğru kaydır. `Esc` kapatır. Seçili dosya yoksa Alt her zamanki gibi çalışır.

| Seçtiğin | Yapabildiklerin |
| --- | --- |
| Görsel | Format değiştir (JPG, PNG, WebP, BMP, TIFF, GIF), ölçeklendir, kırp, sıkıştır (akıllı kalite veya hedef boyut, önce/sonra karşılaştırma), metni al, hazır ayar |
| Video | Sıkıştır, GIF yap, sesi ayır (MP3, M4A, WAV) |
| Klasör | Boyut dökümü, ZIP yap, içindekileri toplu adlandır |
| Her dosya | Yolu kopyala, terminal aç, toplu yeniden adlandır (geri alınabilir), "Kim kullanıyor?" |

Görsel ve video işlemleri yeni dosya yazar; asıl dosyanın üzerine yazılmaz. Birden çok dosya seçersen işlem hepsine uygulanır.

## Renk seçici

Saatin yanındaki ayrı simge:

- **Sol tık**: ekranda bir noktaya tıkla, rengi panoya kopyalansın (ok tuşlarıyla piksel piksel ilerlenir).
- **Sağ tık**: daha önce aldığın renkler.

## Kısayollar

| Kısayol | İşlev |
| --- | --- |
| `Win+Shift+O` | Ekrandan metin al |
| `Win+Shift+U` | Pencereyi üstte sabitle / bırak |
| `Win+Shift+Y` | Düz (biçimsiz) yapıştır |
| `Win+Shift+B` | Büyük/küçük harf çevirip yapıştır |

Menünün açılma süresi, otomatik başlatma ve dosya adlarına eklenecek sabit ek, saatin yanındaki All In One simgesinden **Ayarlar** ile değişir.

## Gizlilik

- Uygulama ağ bağlantısı açmaz.
- Sol Alt'ı fark edebilmek için bir klavye kancası kullanır; bastığın tuşları kaydetmez. Bazı güvenlik yazılımları klavye kancalarından uyarı verebilir.
- Ayarlar ve renk geçmişi `HKCU\Software\AllInOne` altında, hata kaydı `%LOCALAPPDATA%\AllInOne\hata.log` dosyasındadır. Hata kaydında dosya yolları geçebilir; birine göndermeden önce bak.

## Geliştirme

Kaynak, tek klasörde düz C# dosyalarıdır (C# 5, WinForms döngüsü + WPF pencereleri). `kur.ps1` derler ve `AllInOne.exe --test` ile öz testi çalıştırır. Yeni menü araçları `AllInOne.cs` içindeki `Tools` sınıfına eklenir.
