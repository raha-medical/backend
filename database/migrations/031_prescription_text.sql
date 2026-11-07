-- Additive: legacy prescription lines remain unchanged.
ALTER TABLE prescriptions ADD COLUMN IF NOT EXISTS text TEXT;
