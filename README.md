# TTT1 arcade (Namco System 12) — recompilación estática a C#

Proyecto en curso para reconstruir una versión **ejecutable y modificable en Windows x64** de *Tekken Tag Tournament 1 (arcade, Namco System 12)* mediante **recompilación estática**, usando [RecompOne](https://github.com/BlackLabelHQ/RecompOne) como base técnica.

> **Estado real: NO ejecuta el juego todavía.** Hay un recompilador que genera C# a partir de la imagen del juego, un entorno System 12 en construcción y un arnés de pruebas contra MAME. Nada recompilado arranca aún. Este repositorio es un punto de partida, no un resultado final.

## Reglas del proyecto

- Solo se usan los ROMs del propietario (set MAME `tektagt`, TEG2/VER.C1 set 1). **Los ROMs, el código generado, las trazas, la NVRAM y las instantáneas de referencia NO están en este repositorio** (ver `.gitignore`). Se generan en local.
- No se descargan ROMs, BIOS ni filtraciones. El código reconstruido no es el código fuente original de Namco ni debe presentarse como tal.
- El componente del procesador de sonido H8/3002 se **interpreta** (declarado como componente emulado). No se oculta bajo la etiqueta «100 % nativo».
- No se envían aportaciones generadas con IA a BlackLabelHQ (su proyecto no las acepta). Los parches de `patches/` son para uso local.
- No se declara PASS sin evidencia ejecutada. Se distingue «compila», «arranca», «prototipo jugable» y «validado».

## Estado de los hitos

| Hito | Estado | Evidencia |
|---|---|---|
| H1 Set de ROMs completo e identificado | **PASS** | `tools/verify_romset.py` 15/15 por contenido; MAME `-verifyroms tektagt` correcto |
| H0 Herramientas e importador | **FAIL (parcial)** | verificador, regiones y descompresor listos; el importador de System 12 es solo el modo de imagen en bruto (`patches/0001`) |
| H2 Rutinas traducidas + equivalencia | **FAIL** | el descompresor coincide byte a byte con la RAM de MAME; ninguna rutina recompilada se ha comparado aún |
| H3 Arranque hasta pantalla de título | BLOQUEADO | nada recompilado arranca |
| H4 Selección de personaje y combate | BLOQUEADO | referencia en MAME lista (ver abajo) |
| H5 Combate completo | BLOQUEADO | — |
| H6 Audio, controles y estabilidad | BLOQUEADO | — |

## Qué está hecho y probado

- **Arranque:** reset en `0xbfc00000` (ROM principal de 4 MB en `0x1fc00000`). Una rutina de la ROM (`0x1fc21048`) descomprime la imagen del juego desde `0x28000` a `0x80010000` (`0x398350` bytes). Punto de entrada `0x80010000`. `tools/ttt_decompress.py` es una reimplementación independiente cuya salida coincide al 100 % con la RAM de MAME 0.264.
- **ROMs con banco:** el juego solo las lee por el canal 5 del DMA; el registro de banco `0x1f000000` nunca se usa. `BankedRomController` coincide con MAME (1099 operaciones forzadas y 378 449 lecturas reales). La máquina de estados de protección de tektagt está reproducida.
- **Entradas:** llegan por el firmware del H8 a la RAM compartida (`0x1f080000`), no por mandos SIO ni JVS. EEPROM AT28C16 modelada y verificada contra MAME.
- **GPU:** 2 MB de VRAM (1024×1024), 59,826 Hz. Verificado contra capturas de MAME (0 píxeles distintos). RecompOne la fija en 512 filas: ver `patches/0004-gpu-vram.md`.
- **Sonido:** intérprete H8/3002 + mezclador C352 que igualan a MAME 0.264 (los tiempos se desvían tras ~7 s; ver `docs/system12/SOUND.md`).
- **Memoria del runtime:** `System12Memory` con 63 pruebas que pasan.
- **Detector de funciones:** arreglado en modo estricto (1 865 funciones, 23 MB de C#, compila con un parche provisional de la BIOS). El modo no estricto (3 418 funciones, 206 MB) **no compila** (Roslyn se queda sin memoria).
- **Pruebas diferenciales contra MAME:** capturador determinista y comparador con 48 pruebas (+2 con MAME) y 10 de 10 mutaciones detectadas. Falta el lado candidato.
- **Referencia de juego en MAME (para H4):** moneda en los fotogramas 2600–2606, START en 2700–2706, tablero de selección visible en el 2721 y combate real hacia el 3500.

## LO QUE FALTA PARA TERMINARLO (por orden)

1. **Integrar los parches** sobre RecompOne `8d0091c`: `0001` (imagen en bruto), `0002` (detector estricto; hoy solo se activa con `RECOMPONE_STRICT_DETECT=1`, falta una clave en la configuración), `0003` (tipo de memoria configurable), `0004` (VRAM de 2 MB, opcional).
2. **Ganchos de BIOS para arcade.** El código generado llama a `Bios.Break` (542 sitios), `Bios.Syscall` (4) e `Interrupts.Poll` de PS1. Hay que dar equivalentes de arcade en el emisor/runtime. Sin ellos, la salida estricta falla con errores `CS0103/CS0117`.
3. **Escribir el host que una todo y ejecute `Entry`:** `System12Memory` + ROM con banco + DMA diferido + EEPROM + entradas + sonido + GPU/MDEC. Hoy `host/TttHost` solo informa de su estado y dice que el juego NO se ejecutó.
4. **Reescribir lo que no vale de RecompOne para System 12:** temporizadores (usan el reloj real; el juego usa el contador 1 de raster), vblank e interrupciones (solo aceptan < 2 MB), DMA (completa al instante; debe diferirse), canal 5 del DMA, MDEC (la cinemática lo usa; salida sin validar), RAM de 4 MB.
5. **Código que el juego carga más tarde en RAM:** solo el 18,4 % de la imagen es código; hay código cargado en ejecución (zonas `0x8004xxxx`, `0x8008xxxx`, `0x802C...`) que hoy no se emite. Comprobar además las 63 entradas de función que caen en RAM sobrescrita por DMA.
6. **Cobertura frente a MAME:** capturar los PC realmente ejecutados (`tools/mame_pc_capture.*`, `tools/mame_pc_validate.py`; trabajo a medias) y verificar que todos caen dentro de funciones detectadas.
7. **Validación diferencial:** hacer que el runtime vuelque el formato de `docs/difftest/CANDIDATE_SNAPSHOT.md` (registros GTE, hi y lo a 0 al inicio) y comparar con `tools/difftest/compare.py` hasta cero divergencias.
8. **Hitos H3 → H6:** pantalla de título, selección de personaje, combate completo (movimiento, ataques, defensa, cambios de personaje, daño, fin de ronda) y audio/controles estables en varias parejas. Medir tiempo real y velocidad de simulación por separado.
9. **Pendientes menores:** audio al dispositivo del equipo y remuestreo, bus JVS (no usado por tektagt), modo gratis/bloqueo de monedas, qué botones usa Tekken Tag en combate, el firmware de H8 tras 7 s con el reloj de MAME, probar de verdad en Windows (hoy solo se publicó, no se ejecutó) y fijar rutas con espacios.

Estimación honesta: **varias sesiones más**. Los puntos 3 y 4 son el grueso del trabajo.

## Cómo empezar (Windows PowerShell 7+ o bash)

Requisitos: .NET 10 SDK, git, Python 3.8+. Copia tu `tektagt.zip` a `input/roms/tektagt.zip`.

```
scripts/setup.ps1        # clona RecompOne en el commit fijado, aplica patches/*.patch y compila
scripts/import.ps1       # verifica el set, construye regiones y descomprime la imagen -> generated/
scripts/regenerate.ps1   # recompila (con RECOMPONE_STRICT_DETECT=1)                  -> generated/recomp*/
scripts/build.ps1 ; scripts/test.ps1 ; scripts/run.ps1
```

Hay gemelos `.sh`. Comandos verificados y limitaciones: `docs/BUILD.md`. La guía de traspaso para continuar con otra IA o persona es **`HANDOFF.md`**.

## Estructura

| Ruta | Contenido |
|---|---|
| `tools/` | verificador de ROMs, constructor de regiones, descompresor, captura y comparación con MAME |
| `patches/` | parches locales sobre RecompOne `8d0091c` |
| `src/System12.Runtime/` | memoria, ROM con banco, DMA, EEPROM, entradas, sonido H8/C352, adaptador de GPU |
| `host/` | `TttRecompHost` (aún no ejecuta el juego) |
| `scripts/` | pasos reproducibles `.sh` y `.ps1` |
| `docs/system12/` | mapa de direcciones, dispositivos, GPU, sonido, entradas |
| `docs/difftest/` | contrato de la instantánea del lado candidato |
| `reports/` | informes por frente de trabajo y sondas Lua de MAME |
| `tests/` | pruebas en Python (verificador, regiones, descompresor, ROM con banco, difftest) |
| `STATUS.json`, `CONTINUE.md`, `MISSING_INPUTS.md` | estado, siguiente paso y lista exacta de los 15 ROMs necesarios |

## Licencias

RecompOne es MIT (ver `NOTICE`). `C352.cs` deriva de MAME (BSD-3, cabeceras conservadas). Las referencias de hardware salen de MAME (`namcos12.cpp`, commit `f8f6b22`).
