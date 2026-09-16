# Official Employer Interface Version 006 source package

This directory preserves the exact ZIP package supplied for the Employer Interface Version 006 specification before any implementation work is performed against it.

## Original package

- Reconstructed file: `official-employer-interface-006.zip`
- SHA-256: `2cf768191c036031431ce7221ea0bd1ff56532fe59f39d056b4d7b4d2b520c95`
- Size: 22,873 bytes

The exact original ZIP is stored as four Base64 parts because the repository connector only accepts UTF-8 text files. Reconstruct it with:

```bash
cat official-employer-interface-006.zip.b64.part01 \
    official-employer-interface-006.zip.b64.part02 \
    official-employer-interface-006.zip.b64.part03 \
    official-employer-interface-006.zip.b64.part04 \
  | base64 -d > official-employer-interface-006.zip
```

After reconstruction, verify the SHA-256 above before using the package.

## Files in the supplied ZIP

| File | Size | SHA-256 |
| --- | ---: | --- |
| `mimshak_maasikim_mesakem_shnati_xsd_schema_006.xsd.xml` | 44,973 | `8f582a57adf07dd7a7a0885325bac139aaf325522bfad625e069debf887fb73b` |
| `mimshak_maasikim_mesakem_xsd_schema_006.xsd.xml` | 87,031 | `f1995da392f79d46b3d265c7043c736bba3b2714c008a347959705e82156521e` |
| `mimshak_maasikim_shliliim_xsd_schema_006.xsd.xml` | 89,882 | `f930d6bd19ca97fdaf9ec664b4efb70fe7fb0dead32a0f397b2304d0fa282edd` |
| `mimshak_maasikim_shotef_xsd_schema_006.xsd.xml` | 91,511 | `4d4eb66f1cfd26909e5160b06e4549cd36960158ab0b9a8619bf3f0aceb7f7c3` |

Do not substitute earlier interface-version schemas for these files.
