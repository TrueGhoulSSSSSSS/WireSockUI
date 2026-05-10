"""
Procedural Dragon Skeleton Animation
=====================================
A beautiful procedural skeleton dragon that follows the mouse cursor.
Uses inverse kinematics for spine, legs with procedural stepping,
ribs/wings, and a detailed skull.

Controls:
  - Move mouse to guide the dragon's head
  - ESC or close window to quit
"""

import math
import pygame

WIDTH, HEIGHT = 1024, 768
FPS = 60

# Colors
BLACK = (0, 0, 0)
WHITE = (255, 255, 255)
BONE = (220, 220, 210)
JOINT = (200, 200, 190)
DARK_BONE = (180, 180, 170)
GLOW = (40, 40, 50)


class Leg:
    """Procedural leg with IK and stepping behavior."""

    def __init__(self, side: int, anchor_idx: int, is_front: bool):
        self.side = side  # -1 = left, 1 = right
        self.anchor_idx = anchor_idx
        self.is_front = is_front
        self.foot = pygame.Vector2(0, 0)
        self.target = pygame.Vector2(0, 0)
        self.stepping = False
        self.step_progress = 0.0
        self.step_start = pygame.Vector2(0, 0)
        self.step_end = pygame.Vector2(0, 0)
        self.step_height = 30.0
        self.leg_length = 80 if is_front else 90
        self.stride = 70 if is_front else 80

    def update(self, anchor: pygame.Vector2, spine_angle: float, dt: float):
        offset_angle = spine_angle + math.pi / 2 * self.side
        rest_x = anchor.x + math.cos(offset_angle) * self.stride * 0.5
        rest_y = anchor.y + math.sin(offset_angle) * self.stride * 0.5 + self.leg_length * 0.8

        self.target = pygame.Vector2(rest_x, rest_y)

        dist_to_target = self.foot.distance_to(self.target)

        if not self.stepping and dist_to_target > self.stride:
            self.stepping = True
            self.step_progress = 0.0
            self.step_start = pygame.Vector2(self.foot)
            self.step_end = pygame.Vector2(self.target)

        if self.stepping:
            self.step_progress += dt * 5.0
            if self.step_progress >= 1.0:
                self.step_progress = 1.0
                self.stepping = False
                self.foot = pygame.Vector2(self.step_end)
            else:
                t = self.step_progress
                smooth_t = t * t * (3 - 2 * t)
                self.foot.x = self.step_start.x + (self.step_end.x - self.step_start.x) * smooth_t
                self.foot.y = self.step_start.y + (self.step_end.y - self.step_start.y) * smooth_t
                self.foot.y -= math.sin(t * math.pi) * self.step_height


def draw_bone(screen, start, end, thickness=3, color=BONE):
    """Draw a bone segment with joints at both ends."""
    pygame.draw.line(screen, color, start, end, thickness)


def draw_joint(screen, pos, radius=4, color=JOINT):
    """Draw a joint circle."""
    pygame.draw.circle(screen, color, (int(pos.x), int(pos.y)), radius)
    pygame.draw.circle(screen, WHITE, (int(pos.x), int(pos.y)), max(1, radius - 2))


def draw_skull(screen, pos, angle, size=20):
    """Draw a detailed dragon skull."""
    cos_a = math.cos(angle)
    sin_a = math.sin(angle)

    # Skull base (cranium)
    cranium_pts = []
    for i in range(12):
        a = i / 12 * math.pi * 2
        rx = size * 0.7 * (1 + 0.3 * math.cos(a * 2))
        ry = size * 0.5 * (1 + 0.2 * math.sin(a * 3))
        px = rx * math.cos(a)
        py = ry * math.sin(a)
        rotated_x = px * cos_a - py * sin_a + pos.x
        rotated_y = px * sin_a + py * cos_a + pos.y
        cranium_pts.append((rotated_x, rotated_y))
    if len(cranium_pts) >= 3:
        pygame.draw.polygon(screen, DARK_BONE, cranium_pts, 0)
        pygame.draw.polygon(screen, WHITE, cranium_pts, 2)

    # Snout / jaw
    snout_length = size * 1.4
    snout_width = size * 0.35
    snout_tip = pygame.Vector2(
        pos.x + cos_a * snout_length,
        pos.y + sin_a * snout_length
    )
    snout_left = pygame.Vector2(
        pos.x + cos_a * size * 0.4 - sin_a * snout_width,
        pos.y + sin_a * size * 0.4 + cos_a * snout_width
    )
    snout_right = pygame.Vector2(
        pos.x + cos_a * size * 0.4 + sin_a * snout_width,
        pos.y + sin_a * size * 0.4 - cos_a * snout_width
    )
    pygame.draw.polygon(screen, DARK_BONE,
                        [snout_tip, snout_left, snout_right], 0)
    pygame.draw.polygon(screen, WHITE,
                        [snout_tip, snout_left, snout_right], 2)

    # Eye sockets
    eye_offset = size * 0.3
    for side in [-1, 1]:
        eye_x = pos.x + cos_a * size * 0.2 - sin_a * eye_offset * side
        eye_y = pos.y + sin_a * size * 0.2 + cos_a * eye_offset * side
        pygame.draw.circle(screen, BLACK, (int(eye_x), int(eye_y)), int(size * 0.15))
        pygame.draw.circle(screen, WHITE, (int(eye_x), int(eye_y)), int(size * 0.15), 1)

    # Horns
    horn_length = size * 0.8
    for side in [-1, 1]:
        horn_base_x = pos.x - cos_a * size * 0.3 - sin_a * size * 0.3 * side
        horn_base_y = pos.y - sin_a * size * 0.3 + cos_a * size * 0.3 * side
        horn_tip_x = horn_base_x - cos_a * horn_length * 0.3 - sin_a * horn_length * side
        horn_tip_y = horn_base_y - sin_a * horn_length * 0.3 + cos_a * horn_length * side
        pygame.draw.line(screen, WHITE,
                         (int(horn_base_x), int(horn_base_y)),
                         (int(horn_tip_x), int(horn_tip_y)), 3)

    # Teeth
    num_teeth = 5
    for i in range(num_teeth):
        t = (i + 0.5) / num_teeth
        for side in [-1, 1]:
            tx = snout_left.x + (snout_tip.x - snout_left.x) * t if side == -1 else \
                snout_right.x + (snout_tip.x - snout_right.x) * t
            ty = snout_left.y + (snout_tip.y - snout_left.y) * t if side == -1 else \
                snout_right.y + (snout_tip.y - snout_right.y) * t
            tooth_len = size * 0.2 * (1 - t * 0.5)
            tooth_x = tx + sin_a * tooth_len * side
            tooth_y = ty - cos_a * tooth_len * side
            pygame.draw.line(screen, WHITE, (int(tx), int(ty)),
                             (int(tooth_x), int(tooth_y)), 1)


def draw_rib(screen, pos, angle, length, side, taper=1.0):
    """Draw a single rib bone."""
    rib_angle = angle + (math.pi / 2.5) * side
    curve_pts = []
    segments = 6
    for i in range(segments + 1):
        t = i / segments
        cur_angle = rib_angle + t * 0.5 * side
        cur_length = length * t * taper
        px = pos.x + math.cos(cur_angle) * cur_length
        py = pos.y + math.sin(cur_angle) * cur_length
        curve_pts.append((int(px), int(py)))

    if len(curve_pts) >= 2:
        pygame.draw.lines(screen, BONE, False, curve_pts, 2)
        # Joint at tip
        if curve_pts:
            last = curve_pts[-1]
            pygame.draw.circle(screen, JOINT, last, 2)


def draw_leg_ik(screen, anchor, foot, knee_side, is_front):
    """Draw a leg with 2-bone IK (upper + lower leg)."""
    upper_len = 40 if is_front else 45
    lower_len = 40 if is_front else 45

    dx = foot.x - anchor.x
    dy = foot.y - anchor.y
    dist = math.sqrt(dx * dx + dy * dy)
    max_reach = upper_len + lower_len - 2

    if dist > max_reach:
        scale = max_reach / dist
        target = pygame.Vector2(anchor.x + dx * scale, anchor.y + dy * scale)
    else:
        target = pygame.Vector2(foot)

    dx = target.x - anchor.x
    dy = target.y - anchor.y
    dist = math.sqrt(dx * dx + dy * dy)
    if dist < 1:
        dist = 1

    # IK angles
    cos_knee = (upper_len * upper_len + dist * dist - lower_len * lower_len) / (2 * upper_len * dist)
    cos_knee = max(-1, min(1, cos_knee))
    angle_to_target = math.atan2(dy, dx)
    knee_angle = angle_to_target + math.acos(cos_knee) * knee_side

    knee = pygame.Vector2(
        anchor.x + math.cos(knee_angle) * upper_len,
        anchor.y + math.sin(knee_angle) * upper_len
    )

    # Draw bones
    draw_bone(screen, (int(anchor.x), int(anchor.y)), (int(knee.x), int(knee.y)), 3)
    draw_bone(screen, (int(knee.x), int(knee.y)), (int(target.x), int(target.y)), 2)

    # Joints
    draw_joint(screen, anchor, 5)
    draw_joint(screen, knee, 4)
    draw_joint(screen, target, 3)

    # Claws at foot
    foot_angle = math.atan2(target.y - knee.y, target.x - knee.x)
    num_claws = 3
    for i in range(num_claws):
        claw_spread = (i - 1) * 0.3
        claw_angle = foot_angle + claw_spread
        claw_len = 10 if is_front else 12
        claw_x = target.x + math.cos(claw_angle) * claw_len
        claw_y = target.y + math.sin(claw_angle) * claw_len
        pygame.draw.line(screen, WHITE, (int(target.x), int(target.y)),
                         (int(claw_x), int(claw_y)), 1)


def draw_tail_spikes(screen, pos, angle, size):
    """Draw spikes on the tail."""
    for side in [-1, 1]:
        spike_angle = angle + math.pi / 2 * side
        spike_x = pos.x + math.cos(spike_angle) * size
        spike_y = pos.y + math.sin(spike_angle) * size
        pygame.draw.line(screen, BONE, (int(pos.x), int(pos.y)),
                         (int(spike_x), int(spike_y)), 1)


def draw_wing_membrane(screen, spine, start_idx, end_idx, angles, time_val):
    """Draw semi-transparent wing membranes."""
    if end_idx >= len(spine):
        return

    for side in [-1, 1]:
        wing_pts = []
        for i in range(start_idx, end_idx + 1):
            if i >= len(spine):
                break
            pos = spine[i]
            wing_pts.append((int(pos.x), int(pos.y)))

        # Wing extension points
        extended_pts = []
        for i in range(start_idx, end_idx + 1):
            if i >= len(spine):
                break
            pos = spine[i]
            angle = angles[i]
            t = (i - start_idx) / max(1, end_idx - start_idx)
            wing_len = 60 * math.sin(t * math.pi) * (1 + 0.1 * math.sin(time_val * 2 + i * 0.5))
            wing_angle = angle + math.pi / 2 * side - 0.3 * side
            wx = pos.x + math.cos(wing_angle) * wing_len
            wy = pos.y + math.sin(wing_angle) * wing_len
            extended_pts.append((int(wx), int(wy)))

        if extended_pts and wing_pts:
            all_pts = wing_pts + list(reversed(extended_pts))
            if len(all_pts) >= 3:
                # Draw membrane as lines
                for i in range(len(extended_pts) - 1):
                    pygame.draw.line(screen, (60, 60, 70),
                                     extended_pts[i], extended_pts[i + 1], 1)
                # Wing bone struts
                for i in range(0, len(wing_pts), 2):
                    if i < len(extended_pts):
                        pygame.draw.line(screen, DARK_BONE, wing_pts[i], extended_pts[i], 1)


def main():
    pygame.init()
    screen = pygame.display.set_mode((WIDTH, HEIGHT))
    pygame.display.set_caption("Procedural Dragon Skeleton")
    clock = pygame.time.Clock()

    num_vertebrae = 22
    segment_length = 18
    spine = [pygame.Vector2(WIDTH // 2 - i * segment_length, HEIGHT // 2)
             for i in range(num_vertebrae)]
    angles = [0.0] * num_vertebrae

    # Legs: (side, anchor_vertebra_index, is_front)
    legs = [
        Leg(-1, 4, True),
        Leg(1, 4, True),
        Leg(-1, 14, False),
        Leg(1, 14, False),
    ]

    # Initialize foot positions
    for leg in legs:
        anc = spine[leg.anchor_idx]
        leg.foot = pygame.Vector2(anc.x + leg.side * 70, anc.y + 70)

    time_val = 0.0
    trail_positions = []
    max_trail = 5

    running = True
    while running:
        dt = clock.tick(FPS) / 1000.0
        time_val += dt

        for event in pygame.event.get():
            if event.type == pygame.QUIT:
                running = False
            elif event.type == pygame.KEYDOWN:
                if event.key == pygame.K_ESCAPE:
                    running = False

        mouse = pygame.Vector2(pygame.mouse.get_pos())

        # Update spine using inverse kinematics (FABRIK-like)
        # Head follows mouse with smoothing
        head_speed = 8.0
        spine[0] += (mouse - spine[0]) * min(1.0, head_speed * dt)

        # Calculate angles and constrain segments
        for i in range(1, num_vertebrae):
            dx = spine[i].x - spine[i - 1].x
            dy = spine[i].y - spine[i - 1].y
            angle = math.atan2(dy, dx)
            angles[i - 1] = angle - math.pi  # direction toward head

            # Constrain distance
            spine[i].x = spine[i - 1].x + math.cos(angle) * segment_length
            spine[i].y = spine[i - 1].y + math.sin(angle) * segment_length

        # Last angle
        if num_vertebrae > 1:
            dx = spine[-1].x - spine[-2].x
            dy = spine[-1].y - spine[-2].y
            angles[-1] = math.atan2(dy, dx) - math.pi

        # Head angle
        head_angle = angles[0]

        # Update legs
        for leg in legs:
            if leg.anchor_idx < num_vertebrae:
                anchor = spine[leg.anchor_idx]
                spine_angle = angles[leg.anchor_idx]
                leg.update(anchor, spine_angle, dt)

        # Trail effect
        trail_positions.append(pygame.Vector2(spine[0]))
        if len(trail_positions) > max_trail:
            trail_positions.pop(0)

        # === RENDERING ===
        screen.fill(BLACK)

        # Subtle glow behind dragon
        for i in range(0, num_vertebrae, 3):
            glow_radius = 30 - i
            if glow_radius > 5:
                glow_surf = pygame.Surface((glow_radius * 4, glow_radius * 4), pygame.SRCALPHA)
                pygame.draw.circle(glow_surf, (20, 20, 35, 30),
                                   (glow_radius * 2, glow_radius * 2), glow_radius * 2)
                screen.blit(glow_surf, (int(spine[i].x - glow_radius * 2),
                                        int(spine[i].y - glow_radius * 2)))

        # Draw wing membranes
        draw_wing_membrane(screen, spine, 3, 10, angles, time_val)

        # Draw ribs
        for i in range(2, num_vertebrae - 3):
            t = i / num_vertebrae
            rib_length = 35 * (1 - abs(t - 0.4) * 1.5)
            rib_length = max(10, rib_length)
            taper = 1.0 - abs(t - 0.4)
            for side in [-1, 1]:
                draw_rib(screen, spine[i], angles[i], rib_length, side, taper)

        # Draw spine bones
        for i in range(num_vertebrae - 1):
            t = i / num_vertebrae
            thickness = max(2, int(4 * (1 - t * 0.5)))
            draw_bone(screen, (int(spine[i].x), int(spine[i].y)),
                      (int(spine[i + 1].x), int(spine[i + 1].y)),
                      thickness)

        # Draw spine joints (vertebrae)
        for i in range(num_vertebrae):
            t = i / num_vertebrae
            radius = max(2, int(6 * (1 - t * 0.4)))
            draw_joint(screen, spine[i], radius)

        # Draw tail spikes
        for i in range(num_vertebrae - 5, num_vertebrae):
            t = (i - (num_vertebrae - 5)) / 5
            spike_size = 8 * (1 - t)
            if spike_size > 2:
                draw_tail_spikes(screen, spine[i], angles[i], spike_size)

        # Draw tail tip (arrow-like)
        tail_end = spine[-1]
        tail_angle = angles[-1]
        tip_len = 15
        tip = pygame.Vector2(
            tail_end.x + math.cos(tail_angle + math.pi) * tip_len,
            tail_end.y + math.sin(tail_angle + math.pi) * tip_len
        )
        for side in [-1, 1]:
            barb_angle = tail_angle + math.pi + 0.5 * side
            barb = pygame.Vector2(
                tail_end.x + math.cos(barb_angle) * tip_len * 0.7,
                tail_end.y + math.sin(barb_angle) * tip_len * 0.7
            )
            pygame.draw.line(screen, WHITE,
                             (int(tail_end.x), int(tail_end.y)),
                             (int(barb.x), int(barb.y)), 2)
        pygame.draw.line(screen, WHITE,
                         (int(tail_end.x), int(tail_end.y)),
                         (int(tip.x), int(tip.y)), 2)

        # Draw legs
        for leg in legs:
            if leg.anchor_idx < num_vertebrae:
                anchor = spine[leg.anchor_idx]
                knee_side = -1 if leg.is_front else 1
                draw_leg_ik(screen, anchor, leg.foot, knee_side, leg.is_front)

        # Draw skull
        draw_skull(screen, spine[0], head_angle, 22)

        # Draw small particles/dust at feet when stepping
        for leg in legs:
            if leg.stepping and leg.step_progress < 0.3:
                for j in range(3):
                    px = leg.foot.x + (hash((time_val, j)) % 20 - 10)
                    py = leg.foot.y + (hash((time_val, j + 3)) % 10)
                    pygame.draw.circle(screen, (100, 100, 110),
                                       (int(px), int(py)), 1)

        pygame.display.flip()

    pygame.quit()


if __name__ == "__main__":
    main()
