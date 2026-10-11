# MC Download Manager — Windows

Bu depodaki macOS uygulamasının Windows uyarlaması: aynı indirme motoru, aynı tarayıcı köprüsü ve
aynı eklenti; .NET 10 ve [Avalonia](https://avaloniaui.net) ile yeniden yazıldı. [English](README.md)

[`../Extension`](../Extension) klasöründeki Chrome eklentisi değiştirilmeden kullanılır; köprü aynı
portta (`127.0.0.1:48213`) aynı API'yi konuşur. Yani tek eklenti iki uygulamayla da çalışır.

## Özellikler

- **Çok parçalı indirme** — dosya başına 16'ya kadar paralel HTTP `Range` bağlantısı (varsayılan 8);
  sunucu desteklemiyorsa tek akış, `429`/`503` gelirse daha az bağlantı.
- **Duraklat / sürdür** — parça durumu diske yazılır; uygulama yeniden açılınca kaldığı yerden devam eder.
- **Tarayıcı entegrasyonu** — Chrome, Edge, Brave, Opera ve Firefox indirmelerini çerezler, `Referer` ve `User-Agent`
  ile birlikte uygulamaya aktarır. Chromium tarayıcılarda paketlenmemiş eklentinin yüklenmesini bir sihirbaz adım adım anlatır; Firefox, uygulamayla gelen Mozilla imzalı eklentiyi tek tıkla kurar.
- **Video** — `<video>`, `.m3u8` ve `.mpd` akışları yakalanır; "Bu sayfadaki videoyu indir" gömülü
  yt-dlp'yi çalıştırır, gömülü ffmpeg ile birleştirir. yt-dlp Ayarlar'dan güncellenir; YouTube için
  gereken deno istek üzerine, sağlama toplamı doğrulanarak kurulur.
- **Kalite sorusu** — video indirmesi başlamadan önce uygulama o videonun gerçekten sunduğu çözünürlükleri
  boyutlarıyla (X ve Instagram'da tahmin değil, ölçülmüş) ve yalnızca ses (MP3 / M4A) seçeneğini listeler.
  "Hep bunu kullan" seçimi kaydeder. Seçilen çözünürlük dosya adına girer (`Başlık [id] 720p.mp4`); böylece aynı
  video başka kalitede istenince ikinci bir dosya oluşur. macOS uygulamasında yok.
- **Pano** — yakalanan türde bir dosyanın bağlantısı kopyalanınca uygulama sorar; ana pencerede
  `Ctrl+V` her bağlantıyı kabul eder.
- **Bildirim alanında yaşar** — pencere kapansa da köprü açık kalır; istenirse oturum açılışında başlar.
  Arayüz sistem dilini izler: Türkçe ve İngilizce.

## macOS sürümünden farklar

| macOS | Windows |
|---|---|
| Safari eklentisi | yok; yerine Edge destekleniyor |
| Tek tıkla kurulan Firefox eklentisi (1.2.0) | aynı imzalı eklenti (`FirefoxExtension.xpi`, macOS yayınından alınır) exe'nin yanında gelir; sihirbazda "Firefox'a Ekle" |
| Yaş doğrulamalı videolar için uygulama içi giriş penceresi | Ayarlar ▸ Video'dan `cookies.txt` içe aktarma |
| GitHub Releases üzerinden güncelleme denetimi | henüz yok (yt-dlp/deno güncellemesi var) |
| Biten dosyalara karantina özniteliği | Mark of the Web (`Zone.Identifier`); SmartScreen denetler |
| Menü çubuğu simgesi, oturum açma öğesi | bildirim alanı simgesi, `HKCU\…\Run` kaydı |

Parça dosyaları seyrek (sparse) oluşturulur; böylece sekiz bağlantı dosyanın uzak noktalarına yazarken
NTFS aradaki boşlukları önce sıfırla doldurmaz.

## Kurulum

`MC-Download-Manager-<sürüm>-win-x64.zip` dosyasını açın, klasörü kalıcı bir yere taşıyın ve
`MCDownloadManager.exe`'yi çalıştırın. Derleme imzalı olmadığı için SmartScreen bir kez sorar
("Ek bilgi" ▸ "Yine de çalıştır"). Başka bir şey kurmak gerekmez; .NET çalışma ortamı exe'nin içindedir.

`MCDownloadManager.exe --selftest` platforma bağlı kısımları (dosya sistemi, Geri Dönüşüm Kutusu,
köprü portu, kayıt defteri, gömülü araçlar) sınar ve sonucu
`%APPDATA%\MC Download Manager\selftest.txt` dosyasına yazar.

## Derleme

.NET 10 SDK gerekir. Uygulama çapraz derlenir; macOS, Linux ve Windows'ta derlenebilir.

```bash
Windows/scripts/fetch-tools.sh   # yt-dlp.exe ve ffmpeg.exe → Windows/Tools/ (sağlama toplamları doğrulanır)
Windows/scripts/build.sh         # → Windows/build/MC-Download-Manager-<sürüm>-win-x64.zip
# Windows'ta: powershell -ExecutionPolicy Bypass -File Windows\scripts\build.ps1
dotnet test Windows/tests/MCDM.Tests
```

| Yol | İçerik |
|---|---|
| `src/MCDM.Core` | arayüzsüz motor: çok parçalı indirici, kuyruk, yt-dlp sarmalayıcısı, köprü, araç kurucu |
| `src/MCDM.App` | Avalonia arayüzü: ana pencere, dosya penceresi, ayarlar, kurulum sihirbazı, bildirim alanı |
| `tests/MCDM.Tests` | yerel bir sunucuya karşı motor testleri (aralıklar, hız sınırı, kopan bağlantı, yönlendirme) |
| `dev/MCDM.Shots` | her pencereyi ekran dışında PNG'ye çizer; Windows makinesi olmadan yerleşim denetimi için |
| `scripts/extract-strings.py` | `Strings.tr.json` dosyasını macOS dize kataloğundan yeniden üretir |

Türkçe dizeler `App/Resources/Localizable.xcstrings` dosyasından gelir; yalnızca Windows'ta olan dizeler
`src/MCDM.Core/Strings.tr.windows.json` içindedir. Çevirisi olmayan bir arayüz dizesi testi düşürür.

## Lisans

Deponun geri kalanı gibi MIT. Gömülü ikililer kendi lisanslarına tabidir:
[yt-dlp](https://github.com/yt-dlp/yt-dlp) (Unlicense) ve [FFmpeg](https://ffmpeg.org)
(yt-dlp/FFmpeg-Builds, GPL). Simgeler Microsoft Fluent UI System Icons (MIT).
