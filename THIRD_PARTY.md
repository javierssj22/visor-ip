# Componentes de terceros

- **YOLOX**: código de Megvii BaseDetection, Apache-2.0. Se conserva la licencia en `work/training/YOLOX-LICENSE.txt` y el commit de procedencia en `SOURCE.txt`. El subconjunto incluye los modelos, pérdidas y funciones geométricas necesarias para el entrenamiento CPU. Los archivos de inicialización y la utilidad reducida están adaptados para este paquete.
- **ONNX Runtime**: Microsoft, MIT. Se obtiene mediante NuGet y pip. El descargador incluye su licencia junto a los modelos para la distribución Windows.
- **VLC / libVLC**: VideoLAN. Se descarga por separado en Windows y desde el artefacto Maven oficial de libVLC en Android. Conservar sus avisos y cumplir las licencias de los componentes al distribuir binarios; no están incorporados al repositorio.
- **Python, PyTorch, NumPy, Pillow y demás dependencias**: se descargan al preparar el entrenador. Las licencias y metadatos permanecen en sus respectivas distribuciones.

Los modelos preentrenados se descargan de la publicación oficial de YOLOX y no se versionan en Git. No se incluyen fotografías de cámaras ni fotos de pruebas de terceros en este repositorio.
