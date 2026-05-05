# Yarış — `race`

Yarış pisti animasyonu. Katılımcılar (cap 8 lane görünür) yatay
pistlerde renkli arabalar olarak yarışır. Quadratic ease-out ile
hızlanıp yavaşlarlar; her arabanın hızı %85-115 random çarpan.
Kazananın arabası finiş çizgisine ulaşır, diğerleri 3-15% geride
kapatılır. Kazanan araba gold çerçeve + nabız ile parlar.

Kategori: `dramatik` — eliminator'a benzer tansiyon ama tamamen
farklı bir görsel anlatım (eleme yerine "ilk geçen kazanır").

Behaviour:
- Phase A (race): 4500 ms first / 2800 ms subsequent — quadratic ease-out
  (eased = 1 - (1-t)²) for natural racecar acceleration → deceleration profile
- Phase B (finish reveal): woven into Phase A's final frame — winner car
  gets `.won` class (gold border + pulse glow)
- Pause: 900 ms before next winner

Multi-winner: pist temizlenir, geçmiş kazananlar lane havuzundan
çıkarılır (yarışmazlar). Yeni round için arabalar shuffle edilir,
kazanan rastgele bir lane'e yerleşir.

Lane cap: 8. Pool > 8 ise sadece kazanan + 7 diğer rastgele
katılımcı görünür; tüm pool kazanan-seçimi için kullanılır.

Audio: none yet. Phase 2 sound design (engine rev start +
ramping engine loop + horn on win) ships later.
