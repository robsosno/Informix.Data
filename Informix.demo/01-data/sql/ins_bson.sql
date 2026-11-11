-- This script adds product specifications to the stock_spec table.
-- A BSON value is created from JSON text with a double cast.

INSERT INTO stock_spec (stock_num, manu_code, unit, spec)
VALUES (313, 'ANZ', 'box',
        '{"material": "silicone", "colors": ["blue", "red", "white"], "weight_g": 22, "dims": {"width_mm": 210, "height_mm": 190}, "waterproof": true}'::JSON::BSON);

INSERT INTO stock_spec (stock_num, manu_code, unit, spec)
VALUES (312, 'SHM', 'box',
        '{"material": "polycarbonate", "colors": ["black", "clear"], "weight_g": 48, "lens": {"coating": "anti-fog", "uv_protection": true}, "adjustable": true}'::JSON::BSON);

INSERT INTO stock_spec (stock_num, manu_code, unit, spec)
VALUES (308, 'PRC', 'each',
        '{"frame": "aluminium", "weight_kg": 11.5, "wheels": {"count": 3, "diameter_in": 16}, "folds": true, "max_load_kg": 45}'::JSON::BSON);

INSERT INTO stock_spec (stock_num, manu_code, unit, spec)
VALUES (304, 'HRO', 'box',
        '{"display": "analog", "weight_g": 85, "water_resistance_m": 50, "features": ["stopwatch", "backlight"]}'::JSON::BSON);
