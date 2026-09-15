# Source Email attachment retention implementation note

Review Source Email requires a retained download copy for every non-inline attachment that Power Automate sends with content.

The API retains that copy on the archived `email-evidence` payload, using `contentBase64 = attachment.EffectiveContentBase64`.

The paired Web change reads `contentBase64` / `contentBytes` and exposes a `Download copy` link.
