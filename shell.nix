{ pkgs ? import <nixpkgs> {} }:

let
  krb5WithUnversionedLib = pkgs.runCommand "krb5-unversioned-so" {} ''
    mkdir -p $out/lib
    ln -s ${pkgs.krb5}/lib/libgssapi_krb5.so.2 $out/lib/libgssapi_krb5.so
  '';

  # FHS environment that provides /lib, /lib64, /usr/lib etc.
  # so dynamically linked binaries (TizenSdb, .NET native bits) just work.
  fhs = pkgs.buildFHSEnv {
    name = "apps2samsung-fhs";

    targetPkgs = p: with p; [
      # Build and Run
      dotnet-sdk_8
      patchelf
      file
      stdenv.cc.cc.lib
      openssl
      icu
      zlib
      libgcc.lib
      krb5
      krb5WithUnversionedLib

      # Fonts and Rendering
      fontconfig
      freetype
      libGL
      dejavu_fonts
      freefont_ttf

      # X11 tools 
      xorg.libX11
      xorg.libICE
      xorg.libSM
      xorg.libXext
      xorg.libXcursor
      xorg.libXi
      xorg.libXrandr
      xorg.libXrender
      xorg.libXinerama
      xorg.libXcomposite
      xorg.libXdamage
      xorg.libXfixes
      xorg.libXtst

      # Some other packages that may be needed if not installed system wide 
      nmap
      iproute2
      curl
      wget
      xdg-utils
    ];

    runScript = pkgs.writeShellScript "apps2samsung-entry" ''
      export DOTNET_CLI_TELEMETRY_OPTOUT=1
      export DOTNET_NOLOGO=1

      dotnet publish Jellyfin2Samsung-CrossOS/Apps2Samsung.csproj \
          -c Release \
          -r linux-x64 \
          --self-contained true \
          -p:PublishSingleFile=false \
          -p:PublishTrimmed=false

      Jellyfin2Samsung-CrossOS/bin/Release/net8.0/linux-x64/publish/Apps2Samsung
    '';
  };

in
pkgs.mkShell {
  name = "apps2samsung";

  buildInputs = [
    fhs
  ];

  shellHook = ''
    apps2samsung-fhs
  '';
}
