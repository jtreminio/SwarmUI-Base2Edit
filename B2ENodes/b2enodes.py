import time
from PIL import Image
import numpy as np
import comfy.utils
from server import PromptServer, BinaryEventTypes
import io
import struct

"""
Copy of SwarmUI's SwarmSaveImageWS logic, embedded here to avoid importing SwarmComfyCommon
"""

class _SwarmSaveImageWS:
    SPECIAL_ID = 12345  # Tells SwarmUI that this is a "final image" stream

    def save_images(self, images, bit_depth="8bit"):
        # Keep same SPECIAL_ID behavior SwarmUI expects.
        _ = comfy.utils.ProgressBar(self.SPECIAL_ID)
        for image in images:
            if bit_depth == "raw":
                i = 255.0 * image.cpu().numpy()
                img = Image.fromarray(np.clip(i, 0, 255).astype(np.uint8))

                def do_save(out):
                    img.save(out, format="BMP")

                self._send_image_to_server_raw(1, do_save, self.SPECIAL_ID, event_type=10)
            elif bit_depth == "16bit":
                i = 65535.0 * image.cpu().numpy()
                img_np = np.clip(i, 0, 65535).astype(np.uint16)
                try:
                    import cv2

                    img_np = cv2.cvtColor(img_np, cv2.COLOR_BGR2RGB)
                    success, img_encoded = cv2.imencode(".png", img_np)
                    if img_encoded is None or not success:
                        raise RuntimeError("OpenCV failed to encode image.")
                    img_bytes = img_encoded.tobytes()
                except Exception as e:
                    raise RuntimeError(f"Error converting OpenCV image to PNG bytes: {e}")
                self._send_image_to_server_raw(2, lambda out: out.write(img_bytes), self.SPECIAL_ID)
            else:
                i = 255.0 * image.cpu().numpy()
                img = Image.fromarray(np.clip(i, 0, 255).astype(np.uint8))

                def do_save(out):
                    img.save(out, format="PNG")

                self._send_image_to_server_raw(2, do_save, self.SPECIAL_ID)
        return {}

    def _send_image_to_server_raw(
        self,
        type_num: int,
        save_me: callable,
        id: int,
        event_type: int = BinaryEventTypes.PREVIEW_IMAGE,
    ):
        out = io.BytesIO()
        header = struct.pack(">I", type_num)
        out.write(header)
        save_me(out)
        out.seek(0)
        preview_bytes = out.getvalue()
        server = PromptServer.instance
        server.send_sync("progress", {"value": id, "max": id}, sid=server.client_id)
        server.send_sync(event_type, preview_bytes, sid=server.client_id)


class Base2EditSavePreThenPassWS:
    """
    Base2Edit helper node to enforce deterministic ordering:

    - Sends `pre_images` to SwarmUI over websocket first (so it appears first in batch view)
    - Then passes through `post_images` as an IMAGE output so a downstream SwarmSaveImageWS
      will necessarily execute after this node and save the post-edit image second
    """

    @classmethod
    def INPUT_TYPES(s):
        return {
            "required": {
                "pre_images": ("IMAGE",),
                "post_images": ("IMAGE",),
            },
            "optional": {
                "bit_depth": (["8bit", "16bit", "raw"], {"default": "8bit"})
            }
        }

    CATEGORY = "Base2Edit"
    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("images",)
    FUNCTION = "send_then_pass"
    OUTPUT_NODE = False
    DESCRIPTION = "Sends pre-edit images first, then passes post-edit images to be saved second"

    def send_then_pass(self, pre_images, post_images, bit_depth="8bit"):
        _SwarmSaveImageWS().save_images(pre_images, bit_depth)

        return (post_images,)

    @classmethod
    def IS_CHANGED(s, pre_images, post_images, bit_depth="8bit"):
        return time.time()


NODE_CLASS_MAPPINGS = {
    "Base2EditSavePreThenPassWS": Base2EditSavePreThenPassWS,
}
