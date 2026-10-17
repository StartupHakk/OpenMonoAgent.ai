"""24 GB VRAM + 64 GB RAM feasibility table.

File sizes are Hugging Face sibling/tree sizes fetched 2026-10-17. RAM-resident
figures add 8 GiB OS/runtime headroom. No NVIDIA Nemotron rows (Spencer).
"""

from __future__ import annotations

from .types import FeasibilityRow

OS_HEADROOM_GB = 8.0
TARGET_VRAM_GB = 24.0
TARGET_RAM_GB = 64.0

# Hugging Face file sizes (GiB, 1024^3) from the tree API on 2026-10-17.
ROWS: tuple[FeasibilityRow, ...] = (
    FeasibilityRow(
        name="GLM-4.5-Air",
        family="glm45_air",
        quant="UD-IQ2_XXS",
        hf_repo="unsloth/GLM-4.5-Air-GGUF",
        hf_files="GLM-4.5-Air-UD-IQ2_XXS.gguf",
        file_gb=39.75,
        total_params_b=106.0,
        active_params_b=12.0,
        bpw_experts="~2.1 (IQ2_XXS)",
        n_layers=46,
        n_experts=128,
        top_k=8,
        n_shared=1,
        ram_resident_gb=39.75 + OS_HEADROOM_GB,
        vram_attn_gb=10.0,
        ssd_hot_path="none if the 39.75 GB file is loaded into the RAM arena at startup",
        fits_24_64="yes (best large-MoE fit)",
        notes=(
            "First Mosaic adapter. Too big for 24 GB VRAM, fits 64 GB RAM. "
            "Standard attention (no PLE table). IQ3_XXS is 47.91 GB: still RAM-ok, tighter."
        ),
    ),
    FeasibilityRow(
        name="Qwen3.6-35B-A3B",
        family="qwen36",
        quant="UD-Q3_K_M",
        hf_repo="unsloth/Qwen3.6-35B-A3B-GGUF",
        hf_files="Qwen3.6-35B-A3B-UD-Q3_K_M.gguf",
        file_gb=15.46,
        total_params_b=35.0,
        active_params_b=3.0,
        bpw_experts="~3.3 (Q3_K_M); UD-IQ3_S=12.74 GB; UD-Q4_K_XL=20.82 GB",
        n_layers=40,
        n_experts=256,
        top_k=8,
        n_shared=1,
        ram_resident_gb=15.46 + OS_HEADROOM_GB,
        vram_attn_gb=8.0,
        ssd_hot_path="none",
        fits_24_64="yes, but the whole file also fits in 24 GB VRAM",
        notes=(
            "OpenMono already ships UD-Q4_K_XL (20.82 GB live size; catalog still says ~17.6 GB) "
            "on the llama.cpp CPU path. On a 24 GB card llama.cpp can keep the whole model in VRAM, "
            "so a Strata-style split is optional. Hybrid Gated DeltaNet layout is family-specific. "
            "PolyStrata and Strata --family qwen36 already run it."
        ),
    ),
    FeasibilityRow(
        name="Gemma 4 26B-A4B",
        family="gemma4",
        quant="UD-Q3_K_M",
        hf_repo="unsloth/gemma-4-26B-A4B-it-GGUF",
        hf_files="gemma-4-26B-A4B-it-UD-Q3_K_M.gguf",
        file_gb=11.85,
        total_params_b=26.0,
        active_params_b=4.0,
        bpw_experts="~3.3; UD-IQ3_S=10.51 GB; UD-Q4_K_M=15.78 GB",
        n_layers=30,
        n_experts=128,
        top_k=8,
        n_shared=0,
        ram_resident_gb=11.85 + OS_HEADROOM_GB,
        vram_attn_gb=7.0,
        ssd_hot_path="none",
        fits_24_64="yes, whole file fits in 24 GB VRAM",
        notes="PolyStrata strata-poly backend. Needs a 0.43-0.80 GB MTP draft file.",
    ),
    FeasibilityRow(
        name="LFM2.5-24B-A2B",
        family="lfm2moe",
        quant="UD-Q4_K_XL",
        hf_repo="unsloth/LFM2.5-24B-A2B-GGUF",
        hf_files="LFM2.5-24B-A2B-UD-Q4_K_XL.gguf",
        file_gb=13.86,
        total_params_b=24.0,
        active_params_b=2.0,
        bpw_experts="~4.1 (Q4); UD-Q3_K_XL=10.20 GB; UD-IQ3_XXS=8.34 GB",
        n_layers=40,
        n_experts=64,
        top_k=4,
        n_shared=0,
        ram_resident_gb=13.86 + OS_HEADROOM_GB,
        vram_attn_gb=4.0,
        ssd_hot_path="none",
        fits_24_64="yes, whole file fits in 24 GB VRAM",
        notes=(
            "Most layers have no attention, so KV is tiny. License: commercial use only for "
            "companies under $10M yearly revenue. Skip if that is a problem for OpenMono."
        ),
    ),
    FeasibilityRow(
        name="GLM-4.7-Flash",
        family="glm47_flash",
        quant="Q4_K_M",
        hf_repo="unsloth/GLM-4.7-Flash-GGUF",
        hf_files="GLM-4.7-Flash-Q4_K_M.gguf",
        file_gb=17.05,
        total_params_b=30.0,
        active_params_b=3.0,
        bpw_experts="~4 (Q4_K_M); UD-IQ3_XXS=12.02 GB; Q3_K_M=13.61 GB",
        n_layers=0,
        n_experts=0,
        top_k=0,
        n_shared=0,
        ram_resident_gb=17.05 + OS_HEADROOM_GB,
        vram_attn_gb=8.0,
        ssd_hot_path="none",
        fits_24_64="yes at Q3/Q4; likely fits 24 GB VRAM whole",
        notes="Not GLM 5.3 Flash (that one needs ~32 GB VRAM + 96 GB RAM). Adapter later.",
    ),
    FeasibilityRow(
        name="Qwen3-30B-A3B",
        family="qwen3_moe",
        quant="Q3_K_M",
        hf_repo="unsloth/Qwen3-30B-A3B-GGUF",
        hf_files="Qwen3-30B-A3B-Q3_K_M.gguf",
        file_gb=13.70,
        total_params_b=30.0,
        active_params_b=3.0,
        bpw_experts="~3.3; UD-IQ3_XXS=12.00 GB; UD-Q4_K_XL=16.50 GB",
        n_layers=48,
        n_experts=128,
        top_k=8,
        n_shared=0,
        ram_resident_gb=13.70 + OS_HEADROOM_GB,
        vram_attn_gb=8.0,
        ssd_hot_path="none",
        fits_24_64="yes, whole file fits in 24 GB VRAM",
        notes="Superseded for OpenMono by Qwen3.6-35B-A3B on the CPU path.",
    ),
    FeasibilityRow(
        name="Qwen3.8-Flash-Next",
        family="qwen38_flash_next",
        quant="IQ2_XS",
        hf_repo="ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-GGUF",
        hf_files="IQ2_XS shard1 36.53 GB + shard2 26.82 GB",
        file_gb=63.35,
        total_params_b=125.0,
        active_params_b=6.0,
        bpw_experts="~2.3 (IQ2_XS); Q2_0=61.86 GB; IQ3_S=77.87 GB",
        n_layers=48,
        n_experts=512,
        top_k=10,
        n_shared=1,
        ram_resident_gb=36.0 + 26.82 + OS_HEADROOM_GB,
        vram_attn_gb=12.0,
        ssd_hot_path="PLE shard2 is SSD-direct by default in Strata; --ple-io ram needs experts+PLE+8 GiB to fit",
        fits_24_64="partial: experts-in-RAM yes at IQ2_XS; PLE-in-RAM no (36+27+8=71 GiB > 64)",
        notes=(
            "Stays on upstream Strata. 64 GB RAM-resident OpenMono plan uses the Coder "
            "(experts 23 GB + PLE 27 GB + 8 = 58 GiB). IQ2_XS + PLE ram wants ~80 GB class."
        ),
    ),
    FeasibilityRow(
        name="Qwen3.8-Flash-Next Coder",
        family="qwen38_flash_next",
        quant="IQ1_M",
        hf_repo="ISTA-DASLab/Qwen3.8-Flash-Next-GSQ-RCO-Coder-GGUF",
        hf_files="IQ1_M shard1 27.58 GB + shard2 26.82 GB (shared PLE)",
        file_gb=54.40,
        total_params_b=125.0,
        active_params_b=6.0,
        bpw_experts="kept experts stored like IQ3_S; 256 of 512 experts",
        n_layers=48,
        n_experts=256,
        top_k=10,
        n_shared=1,
        ram_resident_gb=23.0 + 26.82 + OS_HEADROOM_GB,
        vram_attn_gb=12.0,
        ssd_hot_path="none if --ple-io ram (fits 64 GB class)",
        fits_24_64="yes as Strata Coder + PLE ram (OpenMono 64 GB default)",
        notes="Weaker outside code / CJK. Coding-agent default on 64 GB boxes that stay on Strata.",
    ),
    FeasibilityRow(
        name="GLM 5.3 Flash",
        family="glm53",
        quant="3.0BIT / UD-IQ2_XXS / REAP50",
        hf_repo="PolyStrata docs (neuralll files)",
        hf_files="79 to 114 GB (PolyStrata README)",
        file_gb=91.0,
        total_params_b=0.0,
        active_params_b=0.0,
        bpw_experts="~2-3",
        n_layers=0,
        n_experts=0,
        top_k=0,
        n_shared=0,
        ram_resident_gb=96.0,
        vram_attn_gb=32.0,
        ssd_hot_path="none if RAM holds it; does not fit 64 GB",
        fits_24_64="no (PolyStrata: 32 GB VRAM + 96 GB RAM)",
        notes="Learn from PolyStrata later. Out of scope for the 24/64 box.",
    ),
    FeasibilityRow(
        name="DeepSeek V4 Flash",
        family="deepseek4",
        quant="UD-IQ2_XXS",
        hf_repo="PolyStrata docs",
        hf_files="91 GB in three files",
        file_gb=91.0,
        total_params_b=0.0,
        active_params_b=0.0,
        bpw_experts="~2",
        n_layers=43,
        n_experts=256,
        top_k=6,
        n_shared=0,
        ram_resident_gb=72.0,
        vram_attn_gb=32.0,
        ssd_hot_path="none if RAM holds it; does not fit 64 GB",
        fits_24_64="no (PolyStrata: 32 GB VRAM + 72 GB RAM)",
        notes="MLA / hybrid attention is family-specific. Out of scope for 24/64.",
    ),
    FeasibilityRow(
        name="Qwen3-235B-A22B",
        family="qwen3_moe",
        quant="UD-Q2_K_XL",
        hf_repo="unsloth/Qwen3-235B-A22B-GGUF",
        hf_files="two shards, 46.42 + 35.55 GB",
        file_gb=81.97,
        total_params_b=235.0,
        active_params_b=22.0,
        bpw_experts="~2.2",
        n_layers=0,
        n_experts=0,
        top_k=8,
        n_shared=0,
        ram_resident_gb=81.97 + OS_HEADROOM_GB,
        vram_attn_gb=16.0,
        ssd_hot_path="would need SSD experts on 64 GB (forbidden)",
        fits_24_64="no",
        notes="Needs ~90 GB RAM-resident. Not a 64 GB target.",
    ),
    FeasibilityRow(
        name="DeepSeek-V3.1",
        family="deepseek3",
        quant="UD-IQ1_S (lowest listed)",
        hf_repo="unsloth/DeepSeek-V3.1-GGUF",
        hf_files="four ~46 GB shards for UD-IQ1_S",
        file_gb=180.0,
        total_params_b=671.0,
        active_params_b=37.0,
        bpw_experts="~1-4 depending on quant",
        n_layers=0,
        n_experts=256,
        top_k=8,
        n_shared=1,
        ram_resident_gb=180.0,
        vram_attn_gb=20.0,
        ssd_hot_path="SSD required on 64 GB (forbidden)",
        fits_24_64="no",
        notes="Server-class even at 1-2 bpw.",
    ),
)


def best_fit_24_64() -> FeasibilityRow:
    """Large MoE that needs RAM offload and still fits 64 GB with no SSD hot path."""
    for row in ROWS:
        if row.family == "glm45_air":
            return row
    raise RuntimeError("glm45_air row missing")


def fits_target(row: FeasibilityRow, ram_gb: float = TARGET_RAM_GB, vram_gb: float = TARGET_VRAM_GB) -> bool:
    if row.ram_resident_gb > ram_gb:
        return False
    if row.vram_attn_gb > vram_gb:
        return False
    if row.fits_24_64.startswith("no"):
        return False
    return True
