# Visor IP

Visor de cámaras IP para Windows, con una aplicación complementaria para Android TV. Interfaz en español.

**Windows 2.8**: reproducción RTSP con VLC, búsqueda de cámaras en la red local, mosaicos de 1/4/6/9/16 cámaras, pantalla completa, PTZ ONVIF según el dispositivo, reconexión y detección local de personas.

El modo guardia recopila 30 imágenes por ciclo (10 entre 06–12, 10 entre 12–20 y 10 entre 20–06). A las 23:30 ofrece revisar la última tanda terminada. Las respuestas humanas alimentan el entrenamiento local; las capturas sin revisar nunca se usan como etiquetas. La alerta roja es fija y los recuadros antiguos caducan.

**Android TV 2.3** conserva su propia implementación. El entrenamiento neuronal y el modo guardia descriptos aquí son de Windows.

## Descargar para usar

En [Releases · Windows 2.8](https://github.com/javierssj22/visor-ip/releases/tag/v2.8.0), descargar **Visor-IP-2.8.0-Windows-x64.zip**.

Extraer todo el ZIP, abrir la carpeta **Visor IP** y ejecutar **Visor IP.exe**. Incluye .NET, VLC, modelos y entrenamiento local: no hace falta instalar Python ni compilar. Conservar todas las carpetas. La primera vez hay que ingresar la red y las credenciales de las cámaras; no se distribuyen configuraciones personales.

Los enlaces **Source code** son para desarrolladores, no el programa listo para ejecutar. La descarga está pensada para Windows de 64 bits con acceso a la red de las cámaras.

## Código y datos

| Carpeta | Contenido |
| --- | --- |
| `work/src` | Aplicación Windows C# / WinForms y pruebas |
| `work/android` | Código Java, recursos y compilación Android TV |
| `work/training/train.py` | Entrenamiento local CPU de la cabeza YOLOX-S |
| `work/training/yolox` | Subconjunto del código oficial YOLOX, con su licencia |
| `scripts` | Preparación y compilación |
| `docs` | Uso y limitaciones |

El repositorio contiene código, no una copia de las cámaras configuradas. No incluye capturas, contraseñas, modelos personales, claves de firma, ejecutables ni dependencias descargadas. El programa listo para ejecutar se distribuye por Releases, con una revisión separada del contenido de cada paquete.

## Compilar Windows

Requisitos: Windows x64, SDK .NET 8 o compatible, Python 3.12+ y acceso a Internet para la preparación inicial.

Desde la raíz del repositorio:

```powershell
python scripts/fetch_models.py
powershell -ExecutionPolicy Bypass -File scripts/build_windows.ps1
```

La aplicación se genera en `work/build`. Copiar la distribución oficial de **VLC para Windows x64** en `work/build/motor`, de modo que existan `motor/libvlc.dll`, `motor/libvlccore.dll` y `motor/plugins/`. No alcanza con copiar sólo una DLL. Los modelos y sus licencias se copian a `work/build/ia` durante la compilación.

Instalar el **.NET Desktop Runtime 8 x64** en el equipo que ejecutará el visor. Luego abrir `work/build/Visor IP.exe`.

### Preparar entrenamiento local (opcional)

Usar **Python 3.12 x64** para preparar los paquetes compatibles con el Python portátil:

```powershell
python scripts/setup_training.py
```

El script descarga el runtime portátil, los pesos oficiales y las dependencias CPU, y copia el entrenador a `work/build/aprendizaje`. Ocupa aproximadamente 1,5 GB. No instala paquetes en el Python global. La preparación inicial usa Internet; el análisis y el entrenamiento posteriores son locales.

## Compilar Android TV

Requisitos: Windows, Python 3, JDK 21, Android SDK con platform 35 y build-tools 36.0.0. Configurar `JAVA_HOME` y `ANDROID_HOME`.

```powershell
python work/android/fetch_deps.py
python work/android/build_apk.py
```

La APK se genera en `work/android/dist`. La primera compilación genera una clave de firma y una contraseña aleatoria dentro de `work/android/signing`, excluida de Git. Conservar esa clave en privado: otra clave no permite actualizar una instalación firmada previamente.

## Pruebas

Pruebas de protocolo con servidores simulados locales, sin cámaras reales:

```powershell
$env:VISOR_TEST_OUTPUT = Join-Path $PWD 'work/test-results'
$p = Start-Process 'work/build/Visor IP.exe' '--self-test' -PassThru
$p.WaitForExit()
Get-Content work/test-results/pruebas-red.txt
```

Las pruebas de video e IA (`--ai-test`, `--auto-capture-test`, `--daily-test`, `--training-test`) requieren VLC, imágenes de referencia y/o un candidato de entrenamiento. Ver [pruebas](docs/PRUEBAS.md) antes de ejecutarlas; no se distribuyen fotografías privadas como fixtures.

## Uso, límites y seguridad

- [Guía para la guardia](docs/GUIA-PARA-LA-GUARDIA.txt)
- [Modo guardia y cambios 2.8](docs/NOVEDADES-2.8.txt)
- [Entrenamiento, validación y límites](docs/NOVEDADES-2.6.txt)
- [Seguridad y datos locales](SECURITY.md)
- [Componentes de terceros](THIRD_PARTY.md)

Para reunir las 30 imágenes deben estar encendidos la PC y el visor y tener video disponible. Los intervalos perdidos no se inventan. El ciclo termina a las 06:00 del día siguiente; la revisión nocturna ofrece la última tanda terminada.

La detección puede omitir personas o producir falsos avisos, especialmente de noche. No identifica personas por su nombre ni determina intenciones. La disponibilidad de PTZ y eventos depende de lo que anuncie cada cámara. Las comprobaciones de la versión 2.8 usaron videos y datos de prueba aislados; no equivalen a una prueba continua de 24 horas ni a una auditoría integral de seguridad.

El código propio aún no tiene una licencia de redistribución elegida. La publicación del repositorio no cambia las licencias de sus componentes de terceros.
