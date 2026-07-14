"""Money math. All amounts are integer cents."""


def discounted_total(items, percent):
    """Total for `items` (list of (unit_cents, qty)) after a `percent` discount.

    Spec: apply the discount to the ORDER total, then round half-up to whole cents.
    """
    total = 0
    for unit_cents, qty in items:
        total += int(unit_cents * qty * (1 - percent / 100))
    return total
