from pricing import discounted_total


def checkout_total(cart, coupon_percent=0):
    """cart: list of (unit_cents, qty). Returns amount to charge, in cents."""
    return discounted_total(cart, coupon_percent)
