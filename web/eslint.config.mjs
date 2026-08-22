import coreWebVitals from 'eslint-config-next/core-web-vitals';
import typescript from 'eslint-config-next/typescript';

/**
 * `next lint` Next 16'da KALDIRILDI: `npm run lint` çalıştırıldığında Next
 * "lint" kelimesini proje dizini sanıp `Invalid project directory provided,
 * no such directory: .../web/lint` diyordu. ESLint zaten kurulu bile değildi,
 * yani betik ilk günden beri hiçbir şey denetlemiyordu — CI de onu hiç
 * çağırmadığı için fark edilmedi (kardeş repo OrderDeck-Mobile'da aynı hata
 * vardı ve aynı şekilde çözüldü).
 *
 * Amaç linti ÇALIŞTIRMAK, kural sıkılaştırmak değil: `next lint`in verdiği
 * setin doğrudan karşılığı olan iki hazır yapılandırma alınıyor, üzerine yeni
 * kural eklenmiyor. Kural tartışması ayrı bir iş.
 *
 * ESLint 9'da sabit: 10 ile `eslint-config-next`in içindeki eslint-plugin-react
 * patlıyor (`contextOrFilename.getFilename is not a function` — ESLint 10'da
 * kaldırılan bağlam API'si). npm 9'u "desteklenmiyor" diye işaretliyor ama
 * çalışan tek bileşim bu; yükseltme eslint-config-next'in kendi bağımlılığını
 * tazelemesine bağlı.
 *
 * Betik `--max-warnings=0` ile koşuyor: bu setin ilgi çekici kurallarının çoğu
 * (`exhaustive-deps`, `no-unused-vars`, `no-img-element`, kullanılmayan
 * disable yönergeleri) UYARI seviyesinde ve `eslint .` uyarıyla 0 döner —
 * yani bayrak olmadan CI adımı çoğu bulguyu görmezden gelirdi. Ağaç şu an
 * temiz olduğu için bedeli sıfır.
 */
const config = [
  {
    ignores: ['.next/**', 'out/**', 'node_modules/**', 'next-env.d.ts'],
  },
  ...coreWebVitals,
  ...typescript,
  {
    rules: {
      // Türkçe metinde kesme işareti ek ayırıcıdır: "chat'lerini", "token'ı",
      // "proxy'nin". Kural bunların hepsini hata sayıyor, yani her kopya
      // düzenlemesinde tetiklenir. JSX'te düz kesme işareti React tarafından
      // zaten doğru render ediliyor; burada güvenlik değil biçim meselesi.
      // Kalıcı bir vergi karşılığında sıfır fayda → kapalı.
      'react/no-unescaped-entities': 'off',
    },
  },
];

export default config;
