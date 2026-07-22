# Migraciones

AI-06 y AI-18 se aplican exclusivamente mediante el migrador independiente. Las
migraciones EF de Identity, Organizations y Locations son adopciones no
destructivas: validan que los objetos canonicos ya existan y registran un
historial separado en `platform`. No recrean, alteran ni eliminan tablas del
baseline.
