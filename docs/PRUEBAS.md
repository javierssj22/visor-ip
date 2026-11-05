# Pruebas del visor

## Protocolos sin cámaras reales

`--self-test` prueba normalización de red, autenticación, protección de XML, RTSP, ONVIF, PTZ y eventos mediante servidores simulados en loopback. Las credenciales de estas pruebas son ficticias.

La salida se escribe en la carpeta indicada por `VISOR_TEST_OUTPUT`. Crear una carpeta bajo `work/test-results`, que está excluida de Git. No guardar resultados en una carpeta versionada.

## Video e IA

Después de preparar `work/build/motor` y los modelos:

1. Preparar imágenes de referencia conocidas en una carpeta fuera de Git.
2. Definir `VISOR_AI_FIXTURES` con esa carpeta. Las pruebas esperan `test-people.jpg` (con personas visibles) y `test-no-person.jpg` (sin personas). Las pruebas de referencia se desarrollaron con la imagen COCO `000000397133` y el ejemplo `assets/dog.jpg` del proyecto YOLOX; una imagen distinta puede necesitar ajustar las aserciones.
3. Definir `VISOR_TEST_OUTPUT` en una carpeta como `work/test-results-v2.7` y ejecutar `--ai-test`. Genera un video de referencia y comprueba seis reproductores VLC, detección, recuadros, rojo fijo, pantalla completa y reconexión. Usa datos aislados.
4. Ejecutar `--auto-capture-test` para confirmar Sí/No/Descartar/Más tarde y el límite de pendientes.
5. Para `--daily-test`, usar como salida `work/test-results-v2.8`; el arnés utiliza el video `personas.avi` de `work/test-results-v2.7` o `work/test-results-v2.6`. Simula horarios y verifica captura/revisión con VLC sin esperar 24 horas.
6. `--training-test` requiere además un candidato ONNX generado por el entrenador, indicado por `VISOR_TRAINED_MODEL`, y `work/build/aprendizaje` preparado. Verifica la instalación del candidato y el arranque/pausa del trabajador con un conjunto aislado.

Estas pruebas abren ventanas. Ejecutarlas en una sesión interactiva de Windows y sin otro ensayo visual simultáneo. Inspeccionar el archivo de resultados: que un proceso termine no basta para afirmar que pasó.

## Diagnóstico de cámaras reales

`--camera-check`, `--events-check` y `--ai-camera-check` usan cámaras guardadas en los datos locales del usuario. Sus resultados son privados: no ejecutarlos en CI pública ni adjuntar sus registros sin revisarlos. No se incluyen datos de cámaras en el repositorio.

## Aprendizaje

El entrenador sólo consume muestras revisadas. La validación separa secuencias de una cámara, exige positivos y negativos suficientes y compara el candidato con el detector previo. Un conjunto de pruebas sintéticas sirve para comprobar que cambian los pesos y que ONNX coincide con PyTorch; no mide la precisión en cámaras reales.
