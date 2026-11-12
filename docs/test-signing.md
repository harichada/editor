# Installing a test-signed package

CI signs the MSIX with a certificate it generates for that run. The certificate is not a paid or trusted publisher certificate. The package will not install until the machine trusts that certificate.

1. Download the `editor-msix-win-x64` or `editor-msix-win-arm64` artifact. It contains the `.msix` and `EditorTest.cer`.
2. Double-click `EditorTest.cer` and install it for the current user into **Trusted People**.
3. Double-click the `.msix`, or run `Add-AppxPackage .\Editor.msix`.

The portable zip is unsigned and does not need this certificate. Run `Editor.exe` from the unpacked folder.
