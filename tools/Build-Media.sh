#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
work="$root/artifacts/media-build"
mkdir -p "$work/sources" "$work/prefix" "$work/output/licenses"
# Archive build scripts must not mistake the containing app repository for upstream Git history.
export GIT_CEILING_DIRECTORIES="$work"
python3 - "$root/tools/media-sources.lock.json" "$work/sources" <<'PY'
import json,hashlib,urllib.request,sys,pathlib
for item in json.load(open(sys.argv[1]))['sources']:
    path=pathlib.Path(sys.argv[2])/f"{item['name']}.tar.gz"
    urllib.request.urlretrieve(item['url'],path)
    assert hashlib.sha256(path.read_bytes()).hexdigest()==item['sha256'],item['name']
PY
for name in ffmpeg x264 libvpx opus; do
  mkdir -p "$work/$name"
  tar xzf "$work/sources/$name.tar.gz" --strip-components=1 -C "$work/$name"
done
export SOURCE_DATE_EPOCH=1756684800
export CC=x86_64-w64-mingw32-gcc-posix AR=x86_64-w64-mingw32-ar RANLIB=x86_64-w64-mingw32-ranlib
export PKG_CONFIG_LIBDIR="$work/prefix/lib/pkgconfig"
export PKG_CONFIG_PATH="$PKG_CONFIG_LIBDIR"
cd "$work/x264"
./configure --host=x86_64-w64-mingw32 --cross-prefix=x86_64-w64-mingw32- --prefix="$work/prefix" --enable-static --disable-cli --disable-opencl
make -j2
make install
cd "$work/libvpx"
export CROSS=x86_64-w64-mingw32-
./configure --target=x86_64-win64-gcc --prefix="$work/prefix" --enable-static --disable-shared --disable-examples --disable-tools --disable-docs --disable-unit-tests --disable-vp8
make -j2
make install
cd "$work/opus"
./autogen.sh
./configure --host=x86_64-w64-mingw32 --prefix="$work/prefix" --enable-static --disable-shared --disable-doc --disable-extra-programs
make -j2
make install
cd "$work/ffmpeg"
./configure --target-os=mingw32 --arch=x86_64 --enable-cross-compile --cross-prefix=x86_64-w64-mingw32- --cc=x86_64-w64-mingw32-gcc-posix --pkg-config=pkg-config --pkg-config-flags=--static --prefix="$work/prefix" --disable-autodetect --enable-gpl --enable-version3 --enable-libx264 --enable-libvpx --enable-libopus --enable-static --disable-shared --disable-network --disable-devices --enable-indev=lavfi --disable-doc --disable-debug --disable-ffplay --disable-iconv --disable-zlib --disable-bzlib --disable-lzma --extra-ldflags=-static
make -j2 ffmpeg.exe ffprobe.exe
cp ffmpeg.exe ffprobe.exe "$work/output/"
cp config.h ffbuild/config.mak "$work/output/"
cp COPYING* LICENSE.md "$work/output/licenses/"
cp /usr/share/doc/mingw-w64-common/copyright "$work/output/licenses/MinGW-runtime-copyright.txt"
cp /usr/share/doc/gcc-mingw-w64-x86-64-posix/copyright "$work/output/licenses/GCC-runtime-copyright.txt"
for name in x264 libvpx opus; do
  mkdir -p "$work/output/licenses/$name"
  find "$work/$name" -maxdepth 1 -type f \( -iname '*copying*' -o -iname '*license*' -o -iname '*patents*' -o -iname '*authors*' \) -exec cp {} "$work/output/licenses/$name/" \;
done
x86_64-w64-mingw32-gcc-posix --version > "$work/output/compiler-version.txt"
dpkg-query -W gcc-mingw-w64-x86-64-posix mingw-w64-common mingw-w64-x86-64-dev nasm make autoconf automake libtool pkg-config > "$work/output/build-packages.txt"
cp "$root/tools/media-sources.lock.json" "$root/tools/Build-Media.sh" "$work/output/"
cd "$work/output"
sha256sum ffmpeg.exe ffprobe.exe > binary-sha256.txt
zip -r "$root/artifacts/media-tools.zip" .
