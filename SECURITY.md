# Seguridad y datos locales

## Qué no se publica

La lista de archivos admitidos por `.gitignore` excluye datos del usuario, capturas, registros de pruebas, preferencias, claves de firma Android, pesos entrenados, runtimes y ejecutables. Evitar `git add -f`: puede saltarse esas exclusiones.

Las credenciales usadas en las pruebas de protocolos son ficticias y se comunican con servidores locales simulados. Las direcciones privadas que aparecen como ejemplos no otorgan acceso a ningún dispositivo.

## Credenciales del visor

En Windows, `AccessVault` usa DPAPI del usuario actual para proteger el acceso guardado. No hay una clave de descifrado incluida en el repositorio. La configuración real reside bajo `%LOCALAPPDATA%/Visor IP`, fuera del código fuente. Android usa su almacén local de claves para el acceso persistido.

Las capturas y las etiquetas de entrenamiento no contienen contraseñas, pero sí imágenes, horarios y la dirección local de la cámara. Son datos privados y no deben añadirse al repositorio. Lo mismo se aplica a las preferencias, incluso si contienen credenciales cifradas.

Los protocolos de muchas cámaras usan HTTP o RTSP sin cifrado de transporte. Usar el visor en una red de confianza; proteger esa red y los dispositivos sigue siendo necesario. El repositorio público no vuelve público el acceso a las cámaras.

## Revisión de publicación

La preparación del primer commit revisa rutas y contenidos del índice de Git. Eso reduce el riesgo de publicar secretos accidentalmente; **no es una garantía de que la aplicación esté libre de vulnerabilidades**.

Si encontrás una vulnerabilidad, no publiques credenciales, direcciones reales, capturas ni detalles de explotación en un issue público. Contactá al propietario por un canal privado acordado.
