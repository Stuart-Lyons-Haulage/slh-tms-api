# Source Email attachment retention validation

The attachment copy is retained only on the immutable `email-evidence` record.

The order staging payload remains metadata-only so one source email with multiple parsed orders does not duplicate large attachment bytes onto every staged order row.

Power Automate must send either `contentBase64` or `contentBytes` after calling **Get attachment content** for each non-inline attachment.
