import math
import urllib.parse
from datetime import datetime, timedelta
from fastapi import FastAPI

# Initialize the API
app = FastAPI(title="P3D Weather Backend")

@app.get("/api/winds")
def get_winds_aloft(lat: float, lon: float):
    # 1. Handle GFS Longitude (NOAA uses 0 to 360 instead of -180 to 180)
    if lon < 0:
        lon += 360
        
    # 2. Calculate the latest GFS model cycle
    # GFS runs every 6 hours (00z, 06z, 12z, 18z). 
    # We look back 5 hours to ensure the file is fully published on NOAA's servers.
    safe_time = datetime.utcnow() - timedelta(hours=5)
    cycle = (safe_time.hour // 6) * 6
    
    date_str = safe_time.strftime("%Y%m%d")
    cycle_str = f"{cycle:02d}"

    # 3. Build a 2-degree bounding box around the aircraft
    # This prevents us from downloading the massive 4GB global file
    box_top = math.ceil(lat) + 1
    box_bottom = math.floor(lat) - 1
    box_left = math.floor(lon) - 1
    box_right = math.ceil(lon) + 1

    # 4. Construct the NOAA NOMADS API parameters
    noaa_url = "https://nomads.ncep.noaa.gov/cgi-bin/filter_gfs_0p25.pl"
    params = {
        "file": f"gfs.t{cycle_str}z.pgrb2.0p25.f000",
        "lev_250_mb": "on", # ~34,000 ft
        "lev_300_mb": "on", # ~30,000 ft
        "lev_400_mb": "on", # ~24,000 ft
        "var_UGRD": "on",   # East/West Wind Component
        "var_VGRD": "on",   # North/South Wind Component
        "var_TMP": "on",    # Temperature
        "subregion": "",
        "leftlon": box_left,
        "rightlon": box_right,
        "toplat": box_top,
        "bottomlat": box_bottom,
        "dir": f"/gfs.{date_str}/{cycle_str}/atmos"
    }
    
    # We will write the actual download/decode logic next.
    # For now, let's just return the generated URL to verify our math.
    full_request_url = noaa_url + "?" + urllib.parse.urlencode(params)

    return {
        "status": "success",
        "aircraft_position": {"lat": lat, "lon": lon},
        "model_cycle": f"{date_str} {cycle_str}Z",
        "target_noaa_url": full_request_url
    }